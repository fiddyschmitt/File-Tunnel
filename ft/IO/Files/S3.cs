using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace ft.IO.Files
{
    public class S3 : IFileAccess
    {
        const string Service = "s3";

        //SHA256 of an empty payload. Used for requests without a body (GET/HEAD/DELETE/COPY).
        const string EmptyPayloadHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

        readonly HttpClient client;
        readonly Uri baseUri;
        readonly string bucket;
        readonly string region;
        readonly string accessKey;
        readonly string secretKey;

        public S3(string endpoint, string region, string bucket, string accessKey, string secretKey)
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
                //so a slow data GET/PUT cannot head-of-line block the others.
                MaxConnectionsPerServer = 20,

                ConnectTimeout = TimeSpan.FromMilliseconds(Program.UNIVERSAL_TIMEOUT_MS),
            };

            client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromMilliseconds(Program.UNIVERSAL_TIMEOUT_MS),
            };
        }

        //Path-style addressing: https://host/bucket/key
        Uri BuildUri(string path)
        {
            var url = $"{baseUri.GetLeftPart(UriPartial.Authority)}{CanonicalUri(path)}";
            var result = new Uri(url, UriKind.Absolute);
            return result;
        }

        string CanonicalUri(string path)
        {
            var key = path.Replace('\\', '/').TrimStart('/');
            var result = $"/{UriEncode(bucket, true)}/{UriEncode(key, false)}";
            return result;
        }

        public bool Exists(string path)
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, BuildUri(path));

            Sign(request, CanonicalUri(path), [], null);
            using var response = client.Send(request);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return false;
            }

            response.EnsureSuccessStatusCode();
            return true;
        }

        public void Delete(string path)
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, BuildUri(path));

            Sign(request, CanonicalUri(path), [], null);
            using var response = client.Send(request);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return;
            }

            response.EnsureSuccessStatusCode();
        }

        public byte[] ReadAllBytes(string path)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BuildUri(path));

            Sign(request, CanonicalUri(path), [], null);
            using var response = client.Send(request);
            response.EnsureSuccessStatusCode();

            using var stream = response.Content.ReadAsStream();
            using var memoryStream = new MemoryStream();
            stream.CopyTo(memoryStream);
            return memoryStream.ToArray();
        }

        public void WriteAllBytes(string path, byte[] bytes, bool overwrite = true)
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, BuildUri(path))
            {
                Content = new ByteArrayContent(bytes)
            };

            var signedHeaders = new SortedDictionary<string, string>(StringComparer.Ordinal);

            if (!overwrite)
            {
                //"*" means: only succeed if no object currently exists at this key
                signedHeaders["if-none-match"] = "*";
            }

            Sign(request, CanonicalUri(path), signedHeaders, bytes);
            using var response = client.Send(request);

            if (!overwrite && response.StatusCode == HttpStatusCode.PreconditionFailed)
            {
                throw new Exception($"{path} exists. Will not overwrite.");
            }

            response.EnsureSuccessStatusCode();
        }

        public void Move(string sourceFileName, string destFileName, bool overwrite)
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

            Sign(request, CanonicalUri(destFileName), signedHeaders, null);
            using (var response = client.Send(request))
            {
                if (!overwrite && response.StatusCode == HttpStatusCode.PreconditionFailed)
                {
                    throw new Exception($"{destFileName} exists. Will not overwrite.");
                }

                response.EnsureSuccessStatusCode();
            }

            Delete(sourceFileName);
        }

        public long GetFileSize(string path)
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, BuildUri(path));

            Sign(request, CanonicalUri(path), [], null);
            using var response = client.Send(request);
            response.EnsureSuccessStatusCode();

            var result = response.Content.Headers.ContentLength ?? 0L;
            return result;
        }

        //Signs the request using AWS Signature Version 4 and applies the required headers.
        //additionalSignedHeaders are both included in the signature and sent on the request.
        void Sign(HttpRequestMessage request, string canonicalUri, SortedDictionary<string, string> additionalSignedHeaders, byte[]? payload)
        {
            var now = DateTime.UtcNow;
            var amzDate = now.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
            var dateStamp = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

            var payloadHash = payload == null ? EmptyPayloadHash : Sha256Hex(payload);

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
                "" + "\n" +                     //no query string for these operations
                canonicalHeaders + "\n" +
                signedHeaders + "\n" +
                payloadHash;

            var credentialScope = $"{dateStamp}/{region}/{Service}/aws4_request";

            var stringToSign =
                "AWS4-HMAC-SHA256\n" +
                amzDate + "\n" +
                credentialScope + "\n" +
                Sha256Hex(Encoding.UTF8.GetBytes(canonicalRequest));

            var signingKey = GetSigningKey(dateStamp);
            var signature = Convert.ToHexString(HmacSha256(signingKey, stringToSign)).ToLowerInvariant();

            var authorization = $"AWS4-HMAC-SHA256 Credential={accessKey}/{credentialScope}, SignedHeaders={signedHeaders}, Signature={signature}";

            request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
            request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        byte[] GetSigningKey(string dateStamp)
        {
            var kDate = HmacSha256(Encoding.UTF8.GetBytes("AWS4" + secretKey), dateStamp);
            var kRegion = HmacSha256(kDate, region);
            var kService = HmacSha256(kRegion, Service);
            var kSigning = HmacSha256(kService, "aws4_request");
            return kSigning;
        }

        static byte[] HmacSha256(byte[] key, string data)
        {
            var result = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));
            return result;
        }

        static string Sha256Hex(byte[] data)
        {
            var result = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
            return result;
        }

        //URI-encodes per RFC 3986 as required by AWS SigV4 (encodes every byte except unreserved chars).
        static string UriEncode(string value, bool encodeSlash)
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
    }
}
