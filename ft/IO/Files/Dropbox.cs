using ft.CLI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ft.IO.Files;

// Native Dropbox backend using the HTTP API v2 - no rclone mount required. Same shape as the S3
// client, with two differences Dropbox forces on us: OAuth2 (short-lived access tokens refreshed
// from a long-lived refresh token) instead of a static key pair, and aggressive rate limiting /
// per-namespace write locks that ft's rapid single-slot overwrite pattern can trip (handled by
// honouring 429 Retry-After).
public class Dropbox : IFileAccess
{
    private const string ApiHost = "https://api.dropboxapi.com";
    private const string ContentHost = "https://content.dropboxapi.com";

    private readonly HttpClient client;
    private readonly string appKey;
    private readonly string appSecret;
    private readonly string refreshToken;

    private readonly SemaphoreSlim tokenAsyncLock = new(1, 1);
    private string accessToken = "";
    private DateTime accessTokenExpiresUtc = DateTime.MinValue;

    public Dropbox(string appKey, string appSecret, string refreshToken)
    {
        this.appKey = appKey;
        this.appSecret = appSecret;
        this.refreshToken = refreshToken;

        var handler = new SocketsHttpHandler
        {
            //Recycle idle keep-alive sockets, matching the S3 client - CDN-fronted hosts can leave
            //stale connections that wedge subsequent requests.
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(15),

            ConnectTimeout = TimeSpan.FromMilliseconds(Program.UNIVERSAL_TIMEOUT_MS),
        };

        client = new HttpClient(handler)
        {
            //Dropbox's per-call latency routinely exceeds the 4s universal timeout the S3 client uses,
            //so bound a single HTTP operation by the (larger, Dropbox-tuned) tunnel timeout instead.
            //DefaultSleepStrategy still enforces the tunnel timeout across retries, so this hides no hang.
            Timeout = TimeSpan.FromMilliseconds(Options.TunnelTimeoutMilliseconds),
        };
    }

    //Dropbox paths are absolute and forward-slashed. ft hands us bare filenames ("1.dat", or
    //"uploads/1.dat"), so normalise to a leading-slash path. For an App-folder app this is relative
    //to the app's own folder; for a Full Dropbox app, to the account root.
    private static string BuildPath(string path)
    {
        var key = path.Replace('\\', '/').TrimStart('/');
        return "/" + key;
    }

    //The request JSON is built by hand rather than with JsonSerializer, whose reflection-based
    //serialization is not trim-safe (the release build is trimmed, which would strip the anonymous
    //types' properties and emit "{}"). JsonDocument on the read side is a DOM parser and is fine.
    private static string J(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }

        sb.Append('"');
        return sb.ToString();
    }

    private static string PathJson(string path) => "{\"path\":" + J(BuildPath(path)) + "}";

    //Sends an authorized request, refreshing the token once on 401 and backing off on 429 (Dropbox's
    //rate-limit / write-lock signal, which carries a Retry-After). buildRequest is re-invoked per
    //attempt because a sent HttpRequestMessage cannot be reused.
    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> buildRequest)
    {
        for (var attempt = 1;; attempt++)
        {
            using var request = buildRequest();
            // TODO: create requestHandler
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GetAccessTokenAsync());

            var response = await client.SendAsync(request);

            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 1)
            {
                accessToken = string.Empty;
                response.Dispose();
                continue;
            }

            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable &&
                attempt <= 3)
            {
                var retryAfter = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(1);
                response.Dispose();

                //Never sleep past the tunnel timeout - beyond that the operation is cancelled anyway.
                var sleepMs = (int)Math.Min(retryAfter.TotalMilliseconds, Options.TunnelTimeoutMilliseconds);
                await Task.Delay(Math.Max(0, sleepMs));
                continue;
            }

            return response;
        }
    }

    private async ValueTask<string> GetAccessTokenAsync()
    {
        if (!string.IsNullOrWhiteSpace(accessToken) && DateTime.UtcNow <= accessTokenExpiresUtc)
        {
            return accessToken;
        }

        await tokenAsyncLock.WaitAsync();
        try
        {
            if (!string.IsNullOrWhiteSpace(accessToken) && DateTime.UtcNow <= accessTokenExpiresUtc)
            {
                return accessToken;
            }

            await RefreshAccessTokenAsync();

            return accessToken;
        }
        finally
        {
            tokenAsyncLock.Release();
        }
    }

    private async Task RefreshAccessTokenAsync()
    {
        await tokenAsyncLock.WaitAsync();

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiHost}/oauth2/token");
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken
            });

            var basic = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{appKey}:{appSecret}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);

            using var response = await client.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                throw new Exception(
                    $"Dropbox token refresh failed ({(int)response.StatusCode}). Check the app key, app secret and refresh token. Response: {body}");
            }

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
            var root = doc.RootElement;

            accessToken = root.GetProperty("access_token").GetString() ?? "";
            var expiresInSeconds = root.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 14400;

            //Refresh a minute early so an in-flight operation never races the expiry.
            accessTokenExpiresUtc = DateTime.UtcNow.AddSeconds(expiresInSeconds - 60);
        }
        finally
        {
            tokenAsyncLock.Release();
        }
    }

    private static StringContent JsonBody(string json) =>
        new(json, Encoding.UTF8, "application/json");

    //A 409 from Dropbox carries a JSON body with an "error_summary" like "path/not_found/..." (or
    //"path_lookup/not_found/..." for delete/move). ReadAsString buffers, so a later EnsureSuccess
    //that re-reads the body still works.
    private static async Task<bool> IsNotFoundAsync(HttpResponseMessage response)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync();
            return body.Contains("not_found", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync();
        throw new Exception($"Dropbox API error ({(int)response.StatusCode}): {body}");
    }

    public async Task<bool> ExistsAsync(string path)
    {
        using var response = await SendAsync(() =>
            new HttpRequestMessage(HttpMethod.Post, $"{ApiHost}/2/files/get_metadata")
            {
                Content = JsonBody(PathJson(path))
            });

        if (response.StatusCode == HttpStatusCode.Conflict && await IsNotFoundAsync(response))
        {
            return false;
        }

        await EnsureSuccessAsync(response);
        return true;
    }

    public async Task DeleteAsync(string path)
    {
        using var response = await SendAsync(() =>
            new HttpRequestMessage(HttpMethod.Post, $"{ApiHost}/2/files/delete_v2")
            {
                Content = JsonBody(PathJson(path))
            });

        // Deleting an absent file is a no-op, as for the other backends.
        if (response.StatusCode == HttpStatusCode.Conflict && await IsNotFoundAsync(response))
        {
            return;
        }

        await EnsureSuccessAsync(response);
    }

    public async Task WriteAllBytesAsync(string path, ReadOnlyMemory<byte> buffer, bool overwrite = true)
    {
        using var response = await SendAsync(() =>
        {
            //mute suppresses the desktop/notification churn from rapid overwrites.
            var arg = "{\"path\":" + J(BuildPath(path))
                                   + ",\"mode\":\"" + (overwrite ? "overwrite" : "add") + "\""
                                   + ",\"autorename\":false,\"mute\":true,\"strict_conflict\":false}";

            var request = new HttpRequestMessage(HttpMethod.Post, $"{ContentHost}/2/files/upload")
            {
                Content = new ReadOnlyMemoryContent(buffer)
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            request.Headers.TryAddWithoutValidation("Dropbox-API-Arg", arg);
            return request;
        });

        // With mode "add", Dropbox returns 409 if the key already exists.
        if (!overwrite && response.StatusCode == HttpStatusCode.Conflict)
        {
            throw new Exception($"{BuildPath(path)} exists. Will not overwrite.");
        }

        await EnsureSuccessAsync(response);
    }

    public async Task MoveAsync(string sourceFileName, string destFileName, bool overwrite)
    {
        //move_v2 fails if the destination exists, so emulate overwrite by clearing it first.
        if (overwrite)
        {
            try
            {
                await DeleteAsync(destFileName);
            }
            catch
            {
                // ignored
            }
        }

        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, $"{ApiHost}/2/files/move_v2")
        {
            Content = JsonBody("{\"from_path\":" + J(BuildPath(sourceFileName))
                                                 + ",\"to_path\":" + J(BuildPath(destFileName)) +
                                                 ",\"autorename\":false}")
        });

        if (!overwrite && response.StatusCode == HttpStatusCode.Conflict && !await IsNotFoundAsync(response))
        {
            throw new Exception($"{BuildPath(destFileName)} exists. Will not overwrite.");
        }

        await EnsureSuccessAsync(response);
    }

    public async Task<Stream> GetStreamAsync(string path)
    {
        //Content endpoints carry their JSON parameters in the Dropbox-API-Arg header, with the raw
        //bytes as the body. A missing file returns 409; EnsureSuccess throws, which UploadDownload's
        //read loop treats as "no data yet" (the same as the S3/FTP backends).
        using var response = await SendAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"{ContentHost}/2/files/download");
            request.Headers.TryAddWithoutValidation("Dropbox-API-Arg", PathJson(path));
            return request;
        });

        await EnsureSuccessAsync(response);

        return await response.Content.ReadAsStreamAsync();
    }

    public async Task<long> GetFileSizeAsync(string path)
    {
        using var response = await SendAsync(() =>
            new HttpRequestMessage(HttpMethod.Post, $"{ApiHost}/2/files/get_metadata")
            {
                Content = JsonBody(PathJson(path))
            });

        await EnsureSuccessAsync(response);

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        return doc.RootElement.TryGetProperty("size", out var size) ? size.GetInt64() : 0L;
    }
}