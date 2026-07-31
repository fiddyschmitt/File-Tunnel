using ft.Socks;
using System;
using System.IO;
using System.Text;

namespace ft.Http;

public sealed class HttpProxyException(string message) : Exception(message);

public sealed class HttpProxyRequest(string destination)
{
    public string Destination { get; } = destination;   // "tcp://host:port"
}

// Minimal HTTP CONNECT proxy handshake - the HTTP analogue of the SOCKS CONNECT path. CONNECT only:
// reads the request line + headers, produces a tcp://host:port destination, and (via the tunnel's
// accurate-reply callback) writes "HTTP/1.1 200 Connection Established" on success or 502/504 on failure.
//
// Reads byte-by-byte and stops exactly at the CRLFCRLF header terminator - never a BinaryReader, which
// would read-ahead and swallow the client's first application bytes (the TLS ClientHello it sends right
// after receiving the 200). Truncated/oversize input throws (never spins), mirroring SocksNegotiator.
public static class HttpProxyNegotiator
{
    private const int MaxHeaderBytes = 64 * 1024;

    public static HttpProxyRequest Read(Stream client)
    {
        var headerBlock = ReadHeaderBlock(client);

        var firstLineEnd = headerBlock.IndexOf("\r\n", StringComparison.Ordinal);
        var requestLine = firstLineEnd >= 0 ? headerBlock[..firstLineEnd] : headerBlock;

        var parts = requestLine.Split(' ');
        if (parts.Length < 3)
        {
            WriteStatus(client, "HTTP/1.1 400 Bad Request");
            throw new HttpProxyException($"Malformed HTTP request line: '{requestLine}'");
        }

        var method = parts[0];
        var target = parts[1];   // CONNECT uses authority-form: host:port (IPv6 as [::1]:443)

        if (!method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
        {
            WriteStatus(client, "HTTP/1.1 405 Method Not Allowed");
            throw new HttpProxyException($"Unsupported HTTP method '{method}' (CONNECT only)");
        }

        if (!target.Contains(':'))
        {
            WriteStatus(client, "HTTP/1.1 400 Bad Request");
            throw new HttpProxyException($"CONNECT target missing port: '{target}'");
        }

        // Already the form NetworkUtilities.ParseEndpoint/AsEndpoint accept ("tcp://host:port",
        // "tcp://[::1]:443"), so hostnames resolve on the exit side exactly like SOCKS.
        return new HttpProxyRequest($"tcp://{target}");
    }

    // Writes the final CONNECT reply carrying the real dial result (a ConnectStatus byte).
    public static void WriteReply(Stream client, byte status)
    {
        var statusLine = (ConnectStatus)status switch
        {
            ConnectStatus.Success => "HTTP/1.1 200 Connection Established",
            ConnectStatus.TtlExpired => "HTTP/1.1 504 Gateway Timeout",
            _ => "HTTP/1.1 502 Bad Gateway"
        };
        WriteStatus(client, statusLine);
    }

    // Reads the request line + headers up to (and including) the CRLFCRLF terminator, one byte at a time
    // so we never consume the client's first payload byte.
    private static string ReadHeaderBlock(Stream client)
    {
        var sb = new StringBuilder();
        var matched = 0;   // progress through the \r \n \r \n terminator
        while (true)
        {
            var b = client.ReadByte();
            if (b < 0) throw new EndOfStreamException("HTTP request truncated");
            sb.Append((char)b);

            if (b == '\r') matched = matched == 2 ? 3 : 1;
            else if (b == '\n') matched = matched == 1 ? 2 : matched == 3 ? 4 : 0;
            else matched = 0;

            if (matched == 4) break;
            if (sb.Length > MaxHeaderBytes) throw new EndOfStreamException("HTTP request headers too large");
        }
        return sb.ToString();
    }

    private static void WriteStatus(Stream client, string statusLine)
    {
        var bytes = Encoding.ASCII.GetBytes(statusLine + "\r\n\r\n");
        client.Write(bytes, 0, bytes.Length);
        client.Flush();
    }
}