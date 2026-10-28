using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace ft_tests.Utilities
{
    // An in-memory S3 bucket for ft's S3 client, in place of its HTTP handler: HEAD, GET (whole or a byte
    // range), PUT and DELETE on path-style keys. Access keys in denyDeletesFor get 403 for every DELETE, like
    // a role without s3:DeleteObject. Once closed, every request blocks for good, which parks the pump threads
    // of the ends using it.
    public sealed class FakeS3Bucket(params string[] denyDeletesFor) : HttpMessageHandler
    {
        public ConcurrentDictionary<string, byte[]> Objects { get; } = new();

        // Non-empty PUTs per key: the files the ends wrote, not the ones they emptied.
        public ConcurrentDictionary<string, int> Writes { get; } = new();

        public int DeleteRequests => deleteRequests;
        int deleteRequests;

        // The S3 error code a refused DELETE carries.
        public string DeniedCode { get; set; } = "AccessDenied";

        readonly ManualResetEventSlim closed = new(false);

        public void Close() => closed.Set();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Send(request, cancellationToken));

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (closed.IsSet) Thread.Sleep(Timeout.Infinite);

            var key = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);

            if (request.Method == HttpMethod.Put)
            {
                using var body = new MemoryStream();
                request.Content?.ReadAsStream(cancellationToken).CopyTo(body);
                Objects[key] = body.ToArray();
                if (body.Length > 0) Writes.AddOrUpdate(key, 1, (_, count) => count + 1);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            if (request.Method == HttpMethod.Delete)
            {
                Interlocked.Increment(ref deleteRequests);

                var authorization = request.Headers.TryGetValues("Authorization", out var values) ? values.First() : "";
                var accessKey = Regex.Match(authorization, "Credential=([^/]+)/").Groups[1].Value;
                if (denyDeletesFor.Contains(accessKey))
                {
                    return new HttpResponseMessage(HttpStatusCode.Forbidden)
                    {
                        Content = new StringContent($"<?xml version=\"1.0\" encoding=\"UTF-8\"?><Error><Code>{DeniedCode}</Code><Message>Access Denied</Message></Error>", Encoding.UTF8, "application/xml")
                    };
                }

                Objects.TryRemove(key, out _);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            if (!Objects.TryGetValue(key, out var content))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            if (request.Method == HttpMethod.Head)
            {
                var head = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
                head.Content.Headers.ContentLength = content.Length;
                return head;
            }

            if (request.Headers.TryGetValues("Range", out var ranges))
            {
                var range = Regex.Match(ranges.First(), @"bytes=(\d+)-(\d+)");
                var first = int.Parse(range.Groups[1].Value);
                if (first >= content.Length) return new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable);

                var last = Math.Min(int.Parse(range.Groups[2].Value), content.Length - 1);
                return new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(content, first, last - first + 1) };
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) };
        }
    }
}
