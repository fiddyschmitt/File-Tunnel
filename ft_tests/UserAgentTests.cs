using CommandLine;
using ft.CLI;
using ft.IO.Files;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ft_tests
{
    /// <summary>
    /// The User-Agent header of the HTTP backends (--user-agent, issue #48). WebDAV and S3 send to a local
    /// listener, so the test sees each request as it went over the wire. Dropbox's hosts are fixed, so it sends
    /// to an in-memory server instead.
    /// </summary>
    [TestClass]
    [TestCategory("Unit")]
    public class UserAgentTests
    {
        // What rclone sends to Yandex Disk, which uploads at full speed only for its own clients. It isn't a valid
        // HTTP product token, so .NET's own parser refuses it.
        const string YandexDisk = """Yandex.Disk {"os":"windows","dtype":"ydisk3","vsn":"3.2.37.4977","id":"6BD01244C7A94456BBCEE7EEC990AEAD","id2":"0F370CD40C594A4783BC839C846B999C","session_id":"0123456789"}""";

        static void Exists(string backend, string url, string userAgent)
        {
            IFileAccess access = backend switch
            {
                "webdav" => new WebDav($"{url}/dav/", "", "", userAgent),
                "s3" => new S3(url, "us-east-1", "bucket", "key", "secret", userAgent: userAgent),
                _ => throw new ArgumentException(backend),
            };

            Assert.IsFalse(access.Exists("1.dat"));
        }

        [TestMethod]
        [DataRow("webdav")]
        [DataRow("s3")]
        public void SendsTheUserAgentAsGiven(string backend)
        {
            var headers = HeadersOfRequest(url => Exists(backend, url, $"  {YandexDisk} "));

            CollectionAssert.Contains(headers, $"User-Agent: {YandexDisk}");
        }

        [TestMethod]
        [DataRow("webdav")]
        [DataRow("s3")]
        public void SendsNoUserAgentByDefault(string backend)
        {
            var headers = HeadersOfRequest(url => Exists(backend, url, ""));

            Assert.IsFalse(headers.Any(header => header.StartsWith("User-Agent:", StringComparison.OrdinalIgnoreCase)), string.Join(" | ", headers));
        }

        [TestMethod]
        public void Dropbox_SendsTheUserAgent()
        {
            var server = new FakeDropbox();
            var dropbox = new Dropbox("app-key", "app-secret", "refresh-token", YandexDisk, server);

            Assert.IsFalse(dropbox.Exists("1.dat"));
            CollectionAssert.AreEqual(new[] { ("/oauth2/token", YandexDisk), ("/2/files/get_metadata", YandexDisk) }, server.Requests);
        }

        [TestMethod]
        [DataRow("", true, "none")]
        [DataRow(YandexDisk, true, "punctuation, such as JSON")]
        [DataRow("ft/1.0\r\nX-Injected: 1", false, "a line break would split the header in two")]
        [DataRow("ft/1.0\t(tunnel)", false, "a control character")]
        [DataRow("ft/1.0 (caf\xE9)", false, ".NET sends nothing beyond ASCII")]
        public void UserAgentIsValid(string userAgent, bool expected, string description)
        {
            Assert.AreEqual(expected, new S3Options { UserAgent = userAgent }.UserAgentIsValid(), description);
        }

        [TestMethod]
        public void TheHttpBackendsTakeTheOption()
        {
            static string UserAgentOf<T>(params string[] args) where T : HttpOptions
            {
                var result = new Parser().ParseArguments<T>([.. args, "-r", "1.dat", "-w", "2.dat", "--user-agent", YandexDisk]);
                Assert.AreEqual(ParserResultType.Parsed, result.Tag, string.Join(", ", result.Errors));
                return result.Value.UserAgent;
            }

            Assert.AreEqual(YandexDisk, UserAgentOf<WebDavOptions>("--webdav", "--url", "https://dav.example.com/"));
            Assert.AreEqual(YandexDisk, UserAgentOf<S3Options>("--s3", "--bucket", "bucket"));
            Assert.AreEqual(YandexDisk, UserAgentOf<DropboxOptions>("--dropbox"));
        }

        // Runs send against a local server, which answers 404, and returns the request line and headers of the
        // first request it received.
        static List<string> HeadersOfRequest(Action<string> send)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();

            try
            {
                var received = Task.Run(() =>
                {
                    using var connection = listener.AcceptTcpClient();
                    var stream = connection.GetStream();

                    var head = new StringBuilder();
                    var buffer = new byte[1];
                    while (!head.ToString().EndsWith("\r\n\r\n") && stream.Read(buffer) == 1)
                    {
                        head.Append((char)buffer[0]);
                    }

                    stream.Write("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\n\r\n"u8);
                    return head.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries).ToList();
                });

                send($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}");

                Assert.IsTrue(received.Wait(TimeSpan.FromSeconds(10)), "The server got no request");
                return received.Result;
            }
            finally
            {
                listener.Stop();
            }
        }

        // Dropbox's token and metadata endpoints: every file is missing. Keeps the path and User-Agent of each request.
        sealed class FakeDropbox : HttpMessageHandler
        {
            public List<(string Path, string UserAgent)> Requests { get; } = [];

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromResult(Send(request, cancellationToken));

            protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var path = request.RequestUri!.AbsolutePath;
                var userAgent = request.Headers.NonValidated.TryGetValues("User-Agent", out var values) ? values.ToString() : "";
                lock (Requests) Requests.Add((path, userAgent));

                return path == "/oauth2/token"
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"access_token":"token","expires_in":14400}""") }
                    : new HttpResponseMessage(HttpStatusCode.Conflict) { Content = new StringContent("""{"error_summary":"path/not_found/"}""") };
            }
        }
    }
}
