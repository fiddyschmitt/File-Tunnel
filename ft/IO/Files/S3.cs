using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace ft.IO.Files;

public class S3 : IFileAccess
{
    private const string Service = "s3";

    //SHA256 of an empty payload. Used for requests without a body (GET/HEAD/DELETE/COPY).
    private const string EmptyPayloadHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    private readonly HttpClient client;
    private readonly Uri baseUri;
    private readonly string bucket;
    private readonly string region;
    private readonly string accessKey;
    private readonly string secretKey;

    public S3(string endpoint, string region, string bucket, string accessKey, string secretKey,
        int maxConnections = 20)
    {
        this.region = string.IsNullOrWhiteSpace(region) ? "us-east-1" : region;
        this.bucket = bucket;
        this.accessKey = accessKey;
        this.secretKey = secretKey;

        if (string.IsNullOrWhiteSpace(endpoint))
        {
            endpoint = $"https://s3.{this.region}.amazonaws.com";
        }

        baseUri = new Uri(endpoint, UriKind.Absolute);

        var handler = new SocketsHttpHandler
        {
            //Recycle idle keep-alive sockets. CDN-fronted endpoints (eg. Bunny, Cloud.ru) can leave
            //stale connections behind that wedge subsequent requests.
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(15),

            //Allow reads, writes and control (ping) traffic to use separate connections concurrently,
            //so a slow data GET/PUT cannot head-of-line block the others. Configurable via --max-connections.
            MaxConnectionsPerServer = maxConnections < 1 ? 1 : maxConnections,

            ConnectTimeout = TimeSpan.FromMilliseconds(Program.UNIVERSAL_TIMEOUT_MS),
        };

        client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMilliseconds(Program.UNIVERSAL_TIMEOUT_MS),
        };
    }

    //Path-style addressing: https://host/bucket/key
    private Uri BuildUri(string path)
    {
        var url = $"{baseUri.GetLeftPart(UriPartial.Authority)}{CanonicalUri(path)}";
        var result = new Uri(url, UriKind.Absolute);
        return result;
    }

    private string CanonicalUri(string path)
    {
        var key = path.Replace('\\', '/').TrimStart('/');
        var result = $"/{UriEncode(bucket, true)}/{UriEncode(key, false)}";
        return result;
    }

    private bool warnedHeadForbidden = false;

    //S3 responds 403 (not 404) to HEAD on a missing key when the credentials lack s3:ListBucket,
    //which makes existence checks throw instead of returning false. A bare "403 Forbidden" reads
    //as a credential problem, so surface the likely fix once. (Benign race: at worst it logs twice.)
    private void WarnIfHeadForbidden(HttpStatusCode statusCode, string path)
    {
        if (statusCode != HttpStatusCode.Forbidden || warnedHeadForbidden) return;
        warnedHeadForbidden = true;

        Program.Log(
            $"S3 returned 403 (Forbidden) for HEAD {path}. Note: S3 returns 403 instead of 404 for a MISSING key when the credentials lack the s3:ListBucket permission, so this may just mean the file isn't there yet. Grant s3:ListBucket on the bucket so existence checks can distinguish missing objects (or check the credentials/clock skew).",
            ConsoleColor.Yellow);
    }

    /// <summary>
    /// Signs the request using AWS Signature Version 4 and applies the required headers.
    /// additionalSignedHeaders are both included in the signature and sent on the request.
    /// </summary>
    private void Sign(HttpRequestMessage request, string canonicalUri,
        SortedDictionary<string, string> additionalSignedHeaders)
    {
        Sign(request, canonicalUri, additionalSignedHeaders, []);
    }

    /// <summary>
    /// Signs the request using AWS Signature Version 4 and applies the required headers.
    /// additionalSignedHeaders are both included in the signature and sent on the request.
    /// </summary>
    private void Sign(HttpRequestMessage request, string canonicalUri,
        SortedDictionary<string, string> additionalSignedHeaders, ReadOnlySpan<byte> payload)
    {
        var now = DateTime.UtcNow;
        var amzDate = now.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
        var dateStamp = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

        var payloadHash = payload.Length == 0 ? EmptyPayloadHash : Sha256Hex(payload);

        var host = request.RequestUri!.IdnHost;
        if (!request.RequestUri.IsDefaultPort)
        {
            host += ":" + request.RequestUri.Port;
        }

        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["host"] = host,
            ["x-amz-content-sha256"] = payloadHash,
            ["x-amz-date"] = amzDate,
        };

        foreach (var kvp in additionalSignedHeaders)
        {
            headers[kvp.Key] = kvp.Value;
            request.Headers.TryAddWithoutValidation(kvp.Key, kvp.Value);
        }

        var canonicalHeaders = new StringBuilder();
        foreach (var kvp in headers)
        {
            canonicalHeaders.Append(kvp.Key).Append(':').Append(kvp.Value.Trim()).Append('\n');
        }

        var signedHeaders = string.Join(";", headers.Keys);

        var canonicalRequest =
            request.Method.Method + "\n" +
            canonicalUri + "\n" +
            "" + "\n" + //no query string for these operations
            canonicalHeaders + "\n" +
            signedHeaders + "\n" +
            payloadHash;

        var credentialScope = $"{dateStamp}/{region}/{Service}/aws4_request";

        var stringToSign =
            "AWS4-HMAC-SHA256\n" +
            amzDate + "\n" +
            credentialScope + "\n" +
            Sha256Hex(Encoding.UTF8.GetBytes(canonicalRequest));

        var signature = ComputeSignature(dateStamp, stringToSign);

        var authorization =
            $"AWS4-HMAC-SHA256 Credential={accessKey}/{credentialScope}, SignedHeaders={signedHeaders}, Signature={signature}";

        request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);
        request.Headers.TryAddWithoutValidation("Authorization", authorization);
    }

    private string ComputeSignature(string dateStamp, string stringToSign)
    {
        Span<byte> signingKeyBuffer = stackalloc byte[256 / 8];
        Span<byte> resultBuffer = stackalloc byte[256 / 8];

        WriteSigningKey(dateStamp, signingKeyBuffer);
        HmacSha256(signingKeyBuffer, stringToSign, resultBuffer);

        return Convert.ToHexStringLower(resultBuffer);
    }

    private void WriteSigningKey(string dateStamp, Span<byte> destination)
    {
        Span<byte> buffer = stackalloc byte[destination.Length];

        HmacSha256(Encoding.ASCII.GetBytes("AWS4" + secretKey), dateStamp, buffer);
        HmacSha256(buffer, region, destination);
        HmacSha256(destination, Service, buffer);
        HmacSha256(buffer, "aws4_request", destination);
    }

    private static void HmacSha256(ReadOnlySpan<byte> key, ReadOnlySpan<char> data, Span<byte> destination)
    {
        var dataBuffer = new byte[data.Length];

        Encoding.ASCII.GetBytes(data, dataBuffer);
        HMACSHA256.HashData(key, dataBuffer, destination);
    }

    private static string Sha256Hex(ReadOnlySpan<byte> data)
    {
        Span<byte> buffer = stackalloc byte[256 / 8];
        SHA256.HashData(data, buffer);

        return Convert.ToHexString(buffer);
    }

    //URI-encodes per RFC 3986 as required by AWS SigV4 (encodes every byte except unreserved chars).
    private static string UriEncode(string value, bool encodeSlash)
    {
        var result = new StringBuilder();

        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;

            if (c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_' or '.' or '~')
            {
                result.Append(c);
            }
            else if (c == '/')
            {
                result.Append(encodeSlash ? "%2F" : "/");
            }
            else
            {
                result.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return result.ToString();
    }

    public async Task<bool> ExistsAsync(string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, BuildUri(path));

        Sign(request, CanonicalUri(path), [], null);
        using var response = await client.SendAsync(request);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        WarnIfHeadForbidden(response.StatusCode, path);
        response.EnsureSuccessStatusCode();
        return true;
    }

    public async Task DeleteAsync(string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, BuildUri(path));

        Sign(request, CanonicalUri(path), [], null);
        using var response = await client.SendAsync(request);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return;
        }

        response.EnsureSuccessStatusCode();
    }

    public async Task WriteAllBytesAsync(string path, ReadOnlyMemory<byte> buffer, bool overwrite = true)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, BuildUri(path));
        request.Content = new ReadOnlyMemoryContent(buffer);

        var signedHeaders = new SortedDictionary<string, string>(StringComparer.Ordinal);

        if (!overwrite)
        {
            //"*" means: only succeed if no object currently exists at this key
            signedHeaders["if-none-match"] = "*";
        }

        Sign(request, CanonicalUri(path), signedHeaders, buffer.Span);
        using var response = await client.SendAsync(request);

        if (!overwrite && response.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            throw new Exception($"{path} exists. Will not overwrite.");
        }

        response.EnsureSuccessStatusCode();
    }

    public async Task MoveAsync(string sourceFileName, string destFileName, bool overwrite)
    {
        //S3 has no native move: copy the object, then delete the source.
        using var request = new HttpRequestMessage(HttpMethod.Put, BuildUri(destFileName));

        var signedHeaders = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["x-amz-copy-source"] = CanonicalUri(sourceFileName)
        };

        if (!overwrite)
        {
            signedHeaders["if-none-match"] = "*";
        }

        Sign(request, CanonicalUri(destFileName), signedHeaders);
        using (var response = await client.SendAsync(request))
        {
            if (!overwrite && response.StatusCode == HttpStatusCode.PreconditionFailed)
            {
                throw new Exception($"{destFileName} exists. Will not overwrite.");
            }

            response.EnsureSuccessStatusCode();
        }

        await DeleteAsync(sourceFileName);
    }

    public async Task<Stream> GetStreamAsync(string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildUri(path));

        Sign(request, CanonicalUri(path), [], null);
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsStreamAsync();
    }

    public async Task<long> GetFileSizeAsync(string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, BuildUri(path));

        Sign(request, CanonicalUri(path), [], null);
        using var response = await client.SendAsync(request);
        WarnIfHeadForbidden(response.StatusCode, path);
        response.EnsureSuccessStatusCode();

        var result = response.Content.Headers.ContentLength ?? 0L;
        return result;
    }
}