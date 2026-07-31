using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace ft.IO.Files;

public class WebDav : IFileAccess
{
    private static readonly HttpMethod MoveMethod = new("MOVE");
    private static readonly HttpMethod PropFindMethod = new("PROPFIND");
    private static readonly XNamespace DavNs = "DAV:";

    private readonly SemaphoreSlim asyncLock = new(1, 1);
    private readonly HttpClient client;
    private readonly Uri baseUri;

    //HEAD support is a fixed server capability; once we learn it's unsupported, stop trying it
    private bool headSupported = true;

    public WebDav(string url, string username, string password)
    {
        if (!url.EndsWith('/'))
        {
            url += "/";
        }

        baseUri = new Uri(url, UriKind.Absolute);

        var handler = new HttpClientHandler
        {
            PreAuthenticate = true, //avoids a 401 challenge round-trip on every request
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

    private Uri BuildUri(string path)
    {
        var relative = path.Replace('\\', '/').TrimStart('/');
        var result = new Uri(baseUri, relative);
        return result;
    }

    private static bool HeadNotSupported(HttpStatusCode statusCode)
    {
        var result = statusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented;
        return result;
    }

    //caller must hold the lock on client
    private async Task<HttpResponseMessage> SendPropFindAsync(Uri uri)
    {
        const string body = """
                            <?xml version="1.0" encoding="utf-8"?>
                            <D:propfind xmlns:D="DAV:"><D:prop><D:getcontentlength/></D:prop></D:propfind>
                            """;

        using var request = new HttpRequestMessage(PropFindMethod, uri);
        request.Content = new StringContent(body, Encoding.UTF8, "application/xml");
        request.Headers.Add("Depth", "0");

        return await client.SendAsync(request);
    }

    //caller must hold the lock on client
    private async Task<bool> PropFindExistsAsync(Uri uri)
    {
        using var response = await SendPropFindAsync(uri);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        response.EnsureSuccessStatusCode();
        return true;
    }

    //caller must hold the lock on client
    private async Task<long> PropFindContentLengthAsync(Uri uri)
    {
        using var response = await SendPropFindAsync(uri);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync();
        var doc = XDocument.Load(stream);

        var contentLengthText = doc
            .Descendants(DavNs + "getcontentlength")
            .Select(element => element.Value)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

        var result = long.TryParse(contentLengthText, out var length) ? length : 0L;
        return result;
    }

    public async Task<bool> ExistsAsync(string path)
    {
        var uri = BuildUri(path);

        await asyncLock.WaitAsync();
        try
        {
            if (!headSupported)
            {
                return await PropFindExistsAsync(uri);
            }

            using var request = new HttpRequestMessage(HttpMethod.Head, uri);
            using var response = await client.SendAsync(request);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return false;
            }

            if (HeadNotSupported(response.StatusCode))
            {
                headSupported = false;
                return await PropFindExistsAsync(uri);
            }

            response.EnsureSuccessStatusCode();
            return true;
        }
        finally
        {
            asyncLock.Release();
        }
    }

    public async Task DeleteAsync(string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, BuildUri(path));

        await asyncLock.WaitAsync();
        try
        {
            using var response = await client.SendAsync(request);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return;
            }

            response.EnsureSuccessStatusCode();
        }
        finally
        {
            asyncLock.Release();
        }
    }

    public async Task WriteAllBytesAsync(string path, ReadOnlyMemory<byte> buffer, bool overwrite = true)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, BuildUri(path));
        request.Content = new ReadOnlyMemoryContent(buffer);

        if (!overwrite)
        {
            //"*" means: only succeed if no entity currently exists at this URL
            request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);
        }

        await asyncLock.WaitAsync();
        try
        {
            using var response = await client.SendAsync(request);

            if (!overwrite && response.StatusCode == HttpStatusCode.PreconditionFailed)
            {
                throw new Exception($"{path} exists. Will not overwrite.");
            }

            response.EnsureSuccessStatusCode();
        }
        finally
        {
            asyncLock.Release();
        }
    }

    public async Task MoveAsync(string sourceFileName, string destFileName, bool overwrite)
    {
        using var request = new HttpRequestMessage(MoveMethod, BuildUri(sourceFileName));
        request.Headers.Add("Destination", BuildUri(destFileName).AbsoluteUri);
        request.Headers.Add("Overwrite", overwrite ? "T" : "F");

        await asyncLock.WaitAsync();
        try
        {
            using var response = await client.SendAsync(request);

            if (!overwrite && response.StatusCode == HttpStatusCode.PreconditionFailed)
            {
                throw new Exception($"{destFileName} exists. Will not overwrite.");
            }

            response.EnsureSuccessStatusCode();
        }
        finally
        {
            asyncLock.Release();
        }
    }

    public async Task<Stream> GetStreamAsync(string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildUri(path));

        await asyncLock.WaitAsync();
        try
        {
            using var response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync();
            var memoryStream = new MemoryStream();
            await stream.CopyToAsync(memoryStream);
            return memoryStream;
        }
        finally
        {
            asyncLock.Release();
        }
    }

    public async Task<long> GetFileSizeAsync(string path)
    {
        var uri = BuildUri(path);

        await asyncLock.WaitAsync();
        try
        {
            if (!headSupported)
            {
                return await PropFindContentLengthAsync(uri);
            }

            using var request = new HttpRequestMessage(HttpMethod.Head, uri);
            using var response = await client.SendAsync(request);

            if (HeadNotSupported(response.StatusCode))
            {
                headSupported = false;
                return await PropFindContentLengthAsync(uri);
            }

            response.EnsureSuccessStatusCode();

            //some servers omit Content-Length on HEAD; fall back to PROPFIND
            var result = response.Content.Headers.ContentLength ?? await PropFindContentLengthAsync(uri);
            return result;
        }
        finally
        {
            asyncLock.Release();
        }
    }
}