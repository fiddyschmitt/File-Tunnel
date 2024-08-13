using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace ft.Utilities
{
    public static class NetworkUtilities
    {
        public static EndPoint ParseEndpoint(string endpointStr)
        {
            EndPoint result;
            if (IPEndPoint.TryParse(endpointStr, out var ipEndpoint))
            {
                result = ipEndpoint;
            }
            else
            {
                var tokens = endpointStr.Split([":"], StringSplitOptions.None);
                if (tokens.Length == 2)
                {
                    result = new DnsEndPoint(tokens[0], int.Parse(tokens[1]));
                }
                else
                {
                    throw new Exception($"Could not parse string to endpoint: {endpointStr}");
                }
            }

            return result;
        }

        public static (string ListenEndpoint, string DestinationEndpoint) ParseForwardString(string forwardStr)
        {
            string? listenEndpoint = null;
            string? destinationEndpoint = null;

            var ipv6Tokens = forwardStr.Split('/');
            if (ipv6Tokens.Length == 3 || ipv6Tokens.Length == 4)
            {
                if (ipv6Tokens.Length == 3)
                {
                    listenEndpoint = $"[::1]:{ipv6Tokens[0]}";
                    destinationEndpoint = $"{ipv6Tokens[1].WrapIfIPV6()}:{ipv6Tokens[2]}";
                }
                else if (ipv6Tokens.Length == 4)
                {
                    listenEndpoint = $"{ipv6Tokens[0].WrapIfIPV6()}:{ipv6Tokens[1]}";
                    destinationEndpoint = $"{ipv6Tokens[2].WrapIfIPV6()}:{ipv6Tokens[3]}";
                }
                else
                {
                    throw new Exception($"Could not process: {forwardStr}");
                }
            }
            else
            {
                var ipv4Tokens = forwardStr.Split(':');
                if (ipv4Tokens.Length == 3)
                {
                    listenEndpoint = $"127.0.0.1:{ipv4Tokens[0]}";
                    destinationEndpoint = $"{ipv4Tokens[1]}:{ipv4Tokens[2]}";
                }
                else if (ipv4Tokens.Length == 4)
                {
                    listenEndpoint = $"{ipv4Tokens[0]}:{ipv4Tokens[1]}";
                    destinationEndpoint = $"{ipv4Tokens[2]}:{ipv4Tokens[3]}";
                }
                else
                {
                    Program.Log($"Please supply arguments using the following syntax:");
                    Program.Log($"-L [bind_address:]port:host:hostport");
                    Program.Log($"Specifies that the given port on the local host is to be forwarded to the given host and port on the remote side. Use forward slashes as separators when using IPV6.");
                    Environment.Exit(1);
                }
            }

            return (listenEndpoint, destinationEndpoint);
        }
    }
}
