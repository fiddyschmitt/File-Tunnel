using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace ft.IO.Files
{
    public class WebDav : IFileAccess
    {
        static readonly HttpMethod MoveMethod = new("MOVE");
        static readonly HttpMethod PropFindMethod = new("PROPFIND");
        static readonly XNamespace DavNs = "DAV:";

        readonly HttpClient client;
        readonly Uri baseUri;

        //HEAD support is a fixed server capability; once we learn it's unsupported, stop trying it
        bool headSupported = true;

        public WebDav(string url, string username, string password)
        {
            if (!url.EndsWith('/'))
            {
                url += "/";
            }

            baseUri = new Uri(url, UriKind.Absolute);

            var handler = new HttpClientHandler
            {
                PreAuthenticate = true,     //avoids a 401 challenge round-trip on every request
            };

            client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromMilliseconds(Program.UNIVERSAL_TIMEOUT_MS),
            };

            if (!string.IsNullOrEmpty(username))
            {
                var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
            }
        }

        Uri BuildUri(string path)
        {
            var relative = path.Replace('\\', '/').TrimStart('/');
            var result = new Uri(baseUri, relative);
            return result;
        }

        public bool Exists(string path)
        {
            var uri = BuildUri(path);

            lock (client)
            {
                if (!headSupported)
                {
                    return PropFindExists(uri);
                }

                using var request = new HttpRequestMessage(HttpMethod.Head, uri);
                using var response = client.Send(request);

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return false;
                }

                if (HeadNotSupported(response.StatusCode))
                {
                    headSupported = false;
                    return PropFindExists(uri);
                }

                response.EnsureSuccessStatusCode();
                return true;
            }
        }

        public void Delete(string path)
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, BuildUri(path));

            lock (client)
            {
                using var response = client.Send(request);

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return;
                }

                response.EnsureSuccessStatusCode();
            }
        }

        public byte[] ReadAllBytes(string path)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BuildUri(path));

            lock (client)
            {
                using var response = client.Send(request);
                response.EnsureSuccessStatusCode();

                using var stream = response.Content.ReadAsStream();
                using var memoryStream = new MemoryStream();
                stream.CopyTo(memoryStream);
                return memoryStream.ToArray();
            }
        }

        public void WriteAllBytes(string path, byte[] bytes, bool overwrite = true)
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, BuildUri(path))
            {
                Content = new ByteArrayContent(bytes)
            };

            if (!overwrite)
            {
                //"*" means: only succeed if no entity currently exists at this URL
                request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);
            }

            lock (client)
            {
                using var response = client.Send(request);

                if (!overwrite && response.StatusCode == HttpStatusCode.PreconditionFailed)
                {
                    throw new Exception($"{path} exists. Will not overwrite.");
                }

                response.EnsureSuccessStatusCode();
            }
        }

        public void Move(string sourceFileName, string destFileName, bool overwrite)
        {
            using var request = new HttpRequestMessage(MoveMethod, BuildUri(sourceFileName));
            request.Headers.Add("Destination", BuildUri(destFileName).AbsoluteUri);
            request.Headers.Add("Overwrite", overwrite ? "T" : "F");

            lock (client)
            {
                using var response = client.Send(request);

                if (!overwrite && response.StatusCode == HttpStatusCode.PreconditionFailed)
                {
                    throw new Exception($"{destFileName} exists. Will not overwrite.");
                }

                response.EnsureSuccessStatusCode();
            }
        }

        public long GetFileSize(string path)
        {
            var uri = BuildUri(path);

            lock (client)
            {
                if (!headSupported)
                {
                    return PropFindContentLength(uri);
                }

                using var request = new HttpRequestMessage(HttpMethod.Head, uri);
                using var response = client.Send(request);

                if (HeadNotSupported(response.StatusCode))
                {
                    headSupported = false;
                    return PropFindContentLength(uri);
                }

                response.EnsureSuccessStatusCode();

                //some servers omit Content-Length on HEAD; fall back to PROPFIND
                var result = response.Content.Headers.ContentLength ?? PropFindContentLength(uri);
                return result;
            }
        }

        static bool HeadNotSupported(HttpStatusCode statusCode)
        {
            var result = statusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented;
            return result;
        }

        //caller must hold the lock on client
        HttpResponseMessage SendPropFind(Uri uri)
        {
            const string body = """
                <?xml version="1.0" encoding="utf-8"?>
                <D:propfind xmlns:D="DAV:"><D:prop><D:getcontentlength/></D:prop></D:propfind>
                """;

            var request = new HttpRequestMessage(PropFindMethod, uri)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/xml")
            };
            request.Headers.Add("Depth", "0");

            var response = client.Send(request);
            return response;
        }

        //caller must hold the lock on client
        bool PropFindExists(Uri uri)
        {
            using var response = SendPropFind(uri);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return false;
            }

            response.EnsureSuccessStatusCode();
            return true;
        }

        //caller must hold the lock on client
        long PropFindContentLength(Uri uri)
        {
            using var response = SendPropFind(uri);
            response.EnsureSuccessStatusCode();

            using var stream = response.Content.ReadAsStream();
            var doc = XDocument.Load(stream);

            var contentLengthText = doc
                .Descendants(DavNs + "getcontentlength")
                .Select(element => element.Value)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

            var result = long.TryParse(contentLengthText, out var length) ? length : 0L;
            return result;
        }
    }
}
