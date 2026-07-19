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

namespace ft.IO.Files
{
    // Native Dropbox backend using the HTTP API v2 - no rclone mount required. Same shape as the S3
    // client, with two differences Dropbox forces on us: OAuth2 (short-lived access tokens refreshed
    // from a long-lived refresh token) instead of a static key pair, and aggressive rate limiting /
    // per-namespace write locks that ft's rapid single-slot overwrite pattern can trip (handled by
    // honouring 429 Retry-After).
    public class Dropbox : IFileAccess
    {
        const string ApiHost = "https://api.dropboxapi.com";
        const string ContentHost = "https://content.dropboxapi.com";

        readonly HttpClient client;
        readonly string appKey;
        readonly string appSecret;
        readonly string refreshToken;

        readonly object tokenLock = new();
        string accessToken = "";
        DateTime accessTokenExpiresUtc = DateTime.MinValue;

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

            //Exchange the refresh token for an access token now, so bad credentials fail here rather than
            //on the first tunnel operation.
            RefreshAccessToken();
        }

        //Dropbox paths are absolute and forward-slashed. ft hands us bare filenames ("1.dat", or
        //"uploads/1.dat"), so normalise to a leading-slash path. For an App-folder app this is relative
        //to the app's own folder; for a Full Dropbox app, to the account root.
        static string BuildPath(string path)
        {
            var key = path.Replace('\\', '/').TrimStart('/');
            return "/" + key;
        }

        //The request JSON is built by hand rather than with JsonSerializer, whose reflection-based
        //serialization is not trim-safe (the release build is trimmed, which would strip the anonymous
        //types' properties and emit "{}"). JsonDocument on the read side is a DOM parser and is fine.
        static string J(string s)
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

        static string PathJson(string path) => "{\"path\":" + J(BuildPath(path)) + "}";

        public bool Exists(string path)
        {
            using var response = Send(() => new HttpRequestMessage(HttpMethod.Post, $"{ApiHost}/2/files/get_metadata")
            {
                Content = JsonBody(PathJson(path))
            });

            if (response.StatusCode == HttpStatusCode.Conflict && IsNotFound(response))
            {
                return false;
            }

            EnsureSuccess(response);
            return true;
        }

        public void Delete(string path)
        {
            using var response = Send(() => new HttpRequestMessage(HttpMethod.Post, $"{ApiHost}/2/files/delete_v2")
            {
                Content = JsonBody(PathJson(path))
            });

            //Deleting an absent file is a no-op, as for the other backends.
            if (response.StatusCode == HttpStatusCode.Conflict && IsNotFound(response))
            {
                return;
            }

            EnsureSuccess(response);
        }

        public byte[] ReadAllBytes(string path)
        {
            //Content endpoints carry their JSON parameters in the Dropbox-API-Arg header, with the raw
            //bytes as the body. A missing file returns 409; EnsureSuccess throws, which UploadDownload's
            //read loop treats as "no data yet" (the same as the S3/FTP backends).
            using var response = Send(() =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, $"{ContentHost}/2/files/download");
                request.Headers.TryAddWithoutValidation("Dropbox-API-Arg", PathJson(path));
                return request;
            });

            EnsureSuccess(response);

            using var stream = response.Content.ReadAsStream();
            using var memoryStream = new MemoryStream();
            stream.CopyTo(memoryStream);
            return memoryStream.ToArray();
        }

        public void WriteAllBytes(string path, byte[] bytes, bool overwrite = true)
        {
            using var response = Send(() =>
            {
                //mute suppresses the desktop/notification churn from rapid overwrites.
                var arg = "{\"path\":" + J(BuildPath(path))
                        + ",\"mode\":\"" + (overwrite ? "overwrite" : "add") + "\""
                        + ",\"autorename\":false,\"mute\":true,\"strict_conflict\":false}";

                var request = new HttpRequestMessage(HttpMethod.Post, $"{ContentHost}/2/files/upload")
                {
                    Content = new ByteArrayContent(bytes)
                };
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                request.Headers.TryAddWithoutValidation("Dropbox-API-Arg", arg);
                return request;
            });

            //With mode "add", Dropbox returns 409 if the key already exists.
            if (!overwrite && response.StatusCode == HttpStatusCode.Conflict)
            {
                throw new Exception($"{BuildPath(path)} exists. Will not overwrite.");
            }

            EnsureSuccess(response);
        }

        public void Move(string sourceFileName, string destFileName, bool overwrite)
        {
            //move_v2 fails if the destination exists, so emulate overwrite by clearing it first.
            if (overwrite)
            {
                try { Delete(destFileName); } catch { }
            }

            using var response = Send(() => new HttpRequestMessage(HttpMethod.Post, $"{ApiHost}/2/files/move_v2")
            {
                Content = JsonBody("{\"from_path\":" + J(BuildPath(sourceFileName))
                        + ",\"to_path\":" + J(BuildPath(destFileName)) + ",\"autorename\":false}")
            });

            if (!overwrite && response.StatusCode == HttpStatusCode.Conflict && !IsNotFound(response))
            {
                throw new Exception($"{BuildPath(destFileName)} exists. Will not overwrite.");
            }

            EnsureSuccess(response);
        }

        public long GetFileSize(string path)
        {
            using var response = Send(() => new HttpRequestMessage(HttpMethod.Post, $"{ApiHost}/2/files/get_metadata")
            {
                Content = JsonBody(PathJson(path))
            });

            EnsureSuccess(response);

            using var stream = response.Content.ReadAsStream();
            using var doc = JsonDocument.Parse(stream);
            return doc.RootElement.TryGetProperty("size", out var size) ? size.GetInt64() : 0L;
        }

        //Sends an authorized request, refreshing the token once on 401 and backing off on 429 (Dropbox's
        //rate-limit / write-lock signal, which carries a Retry-After). buildRequest is re-invoked per
        //attempt because a sent HttpRequestMessage cannot be reused.
        HttpResponseMessage Send(Func<HttpRequestMessage> buildRequest)
        {
            for (var attempt = 1; ; attempt++)
            {
                var request = buildRequest();
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", GetAccessToken());

                HttpResponseMessage response;
                try
                {
                    response = client.Send(request);
                }
                finally
                {
                    request.Dispose();
                }

                if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 1)
                {
                    response.Dispose();
                    RefreshAccessToken();
                    continue;
                }

                if (((int)response.StatusCode == 429 || response.StatusCode == HttpStatusCode.ServiceUnavailable) && attempt <= 3)
                {
                    var retryAfter = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(1);
                    response.Dispose();

                    //Never sleep past the tunnel timeout - beyond that the operation is cancelled anyway.
                    var sleepMs = Math.Min(retryAfter.TotalMilliseconds, Options.TunnelTimeoutMilliseconds);
                    Thread.Sleep((int)Math.Max(0, sleepMs));
                    continue;
                }

                return response;
            }
        }

        string GetAccessToken()
        {
            lock (tokenLock)
            {
                if (string.IsNullOrEmpty(accessToken) || DateTime.UtcNow >= accessTokenExpiresUtc)
                {
                    RefreshAccessToken();
                }
                return accessToken;
            }
        }

        void RefreshAccessToken()
        {
            lock (tokenLock)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiHost}/oauth2/token")
                {
                    Content = new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["grant_type"] = "refresh_token",
                        ["refresh_token"] = refreshToken
                    })
                };

                var basic = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{appKey}:{appSecret}"));
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);

                using var response = client.Send(request);
                if (!response.IsSuccessStatusCode)
                {
                    var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    throw new Exception($"Dropbox token refresh failed ({(int)response.StatusCode}). Check the app key, app secret and refresh token. Response: {body}");
                }

                using var stream = response.Content.ReadAsStream();
                using var doc = JsonDocument.Parse(stream);
                var root = doc.RootElement;

                accessToken = root.GetProperty("access_token").GetString() ?? "";
                var expiresInSeconds = root.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 14400;

                //Refresh a minute early so an in-flight operation never races the expiry.
                accessTokenExpiresUtc = DateTime.UtcNow.AddSeconds(expiresInSeconds - 60);
            }
        }

        static StringContent JsonBody(string json) =>
            new(json, Encoding.UTF8, "application/json");

        //A 409 from Dropbox carries a JSON body with an "error_summary" like "path/not_found/..." (or
        //"path_lookup/not_found/..." for delete/move). ReadAsString buffers, so a later EnsureSuccess
        //that re-reads the body still works.
        static bool IsNotFound(HttpResponseMessage response)
        {
            try
            {
                var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                return body.Contains("not_found", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        static void EnsureSuccess(HttpResponseMessage response)
        {
            if (response.IsSuccessStatusCode)
            {
                return;
            }

            var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            throw new Exception($"Dropbox API error ({(int)response.StatusCode}): {body}");
        }
    }
}
