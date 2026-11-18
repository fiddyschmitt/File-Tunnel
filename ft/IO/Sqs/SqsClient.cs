using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace ft.IO.Sqs
{
    public class SqsMessage
    {
        public string MessageId { get; set; } = "";
        public string ReceiptHandle { get; set; } = "";
        public string Body { get; set; } = "";
    }

    public class SqsClient
    {
        const string Service = "sqs";
        readonly HttpClient client;
        readonly string region;
        readonly string accessKey;
        readonly string secretKey;

        public SqsClient(string region, string accessKey, string secretKey, int maxConnections = 20)
        {
            this.region = string.IsNullOrWhiteSpace(region) ? "us-east-1" : region;
            this.accessKey = accessKey;
            this.secretKey = secretKey;

            var handler = new SocketsHttpHandler
            {
                PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
                MaxConnectionsPerServer = maxConnections < 1 ? 1 : maxConnections,
                ConnectTimeout = TimeSpan.FromMilliseconds(4000),
                UseProxy = false,
                UseCookies = false,
                AllowAutoRedirect = false
            };

            client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(35),
            };

            client.DefaultRequestHeaders.ExpectContinue = false;
        }

        public void SendMessage(string queueUrl, string messageBody)
        {
            var parameters = new Dictionary<string, string>
            {
                { "Action", "SendMessage" },
                { "QueueUrl", queueUrl },
                { "MessageBody", messageBody },
                { "Version", "2012-11-05" }
            };

            SendRequest(queueUrl, parameters);
        }

        public List<SqsMessage> ReceiveMessages(string queueUrl, int maxMessages = 10, int waitTimeSeconds = 20)
        {
            var parameters = new Dictionary<string, string>
            {
                { "Action", "ReceiveMessage" },
                { "QueueUrl", queueUrl },
                { "MaxNumberOfMessages", maxMessages.ToString() },
                { "WaitTimeSeconds", waitTimeSeconds.ToString() },
                { "Version", "2012-11-05" }
            };

            var responseXml = SendRequest(queueUrl, parameters);
            return ParseReceiveMessageResponse(responseXml);
        }

        public void DeleteMessage(string queueUrl, string receiptHandle)
        {
            var parameters = new Dictionary<string, string>
            {
                { "Action", "DeleteMessage" },
                { "QueueUrl", queueUrl },
                { "ReceiptHandle", receiptHandle },
                { "Version", "2012-11-05" }
            };

            SendRequest(queueUrl, parameters);
        }

        string SendRequest(string queueUrl, Dictionary<string, string> parameters)
        {
            var queueUri = new Uri(queueUrl, UriKind.Absolute);
            var baseUri = new Uri($"{queueUri.Scheme}://{queueUri.Authority}/");

            using var request = new HttpRequestMessage(HttpMethod.Post, baseUri);

            var sortedParams = parameters.OrderBy(kvp => kvp.Key, StringComparer.Ordinal);
            var bodyString = string.Join("&", sortedParams.Select(kvp => $"{UriEncode(kvp.Key)}={UriEncode(kvp.Value)}"));
            var bodyBytes = Encoding.UTF8.GetBytes(bodyString);

            request.Content = new ByteArrayContent(bodyBytes);
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-www-form-urlencoded");

            Sign(request, baseUri, bodyBytes);

            using var response = client.Send(request);
            var responseContent = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

            if (!response.IsSuccessStatusCode)
            {
                throw new Exception($"SQS API error ({(int)response.StatusCode}): {responseContent}");
            }

            return responseContent;
        }

        void Sign(HttpRequestMessage request, Uri uri, byte[] payload)
        {
            var now = DateTime.UtcNow;
            var amzDate = now.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
            var dateStamp = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

            var payloadHash = Sha256Hex(payload);

            var host = uri.IdnHost;
            if (!uri.IsDefaultPort)
            {
                host += ":" + uri.Port;
            }

            var canonicalUri = uri.AbsolutePath;
            if (string.IsNullOrEmpty(canonicalUri)) canonicalUri = "/";

            var headers = new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["content-type"] = "application/x-www-form-urlencoded",
                ["host"] = host,
                ["x-amz-content-sha256"] = payloadHash,
                ["x-amz-date"] = amzDate,
            };

            var canonicalHeaders = new StringBuilder();
            foreach (var kvp in headers)
            {
                canonicalHeaders.Append(kvp.Key).Append(':').Append(kvp.Value.Trim()).Append('\n');
            }

            var signedHeaders = string.Join(";", headers.Keys);

            var canonicalRequest =
                request.Method.Method + "\n" +
                canonicalUri + "\n" +
                "" + "\n" +
                canonicalHeaders.ToString() + "\n" +
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

            request.Headers.TryAddWithoutValidation("Authorization", authorization);
            request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
            request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);
        }

        byte[] GetSigningKey(string dateStamp)
        {
            var kDate = HmacSha256(Encoding.UTF8.GetBytes("AWS4" + secretKey), dateStamp);
            var kRegion = HmacSha256(kDate, region);
            var kService = HmacSha256(kRegion, Service);
            var kSigning = HmacSha256(kService, "aws4_request");
            return kSigning;
        }

        static byte[] HmacSha256(byte[] key, string data) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));

        static string Sha256Hex(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

        static string UriEncode(string value)
        {
            var result = new StringBuilder();
            foreach (var b in Encoding.UTF8.GetBytes(value))
            {
                var c = (char)b;
                if (c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_' or '.' or '~')
                {
                    result.Append(c);
                }
                else
                {
                    result.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
                }
            }
            return result.ToString();
        }

        List<SqsMessage> ParseReceiveMessageResponse(string xml)
        {
            var messages = new List<SqsMessage>();
            try
            {
                var doc = XDocument.Parse(xml);
                XNamespace ns = doc.Root?.Name.Namespace ?? string.Empty;

                foreach (var msgNode in doc.Descendants(ns + "Message"))
                {
                    messages.Add(new SqsMessage
                    {
                        MessageId = msgNode.Element(ns + "MessageId")?.Value ?? "",
                        ReceiptHandle = msgNode.Element(ns + "ReceiptHandle")?.Value ?? "",
                        Body = msgNode.Element(ns + "Body")?.Value ?? ""
                    });
                }
            }
            catch { }
            return messages;
        }
    }
}
