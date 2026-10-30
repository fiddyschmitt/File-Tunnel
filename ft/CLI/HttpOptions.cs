using CommandLine;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ft.CLI
{
    //The options of the backends that talk to their server over HTTP: WebDAV, S3 and Dropbox.
    public abstract class HttpOptions : Options
    {
        [Option("user-agent", Required = false, HelpText = @"The User-Agent header to send with each request. Some services only accept, or only serve at full speed, the clients they recognise. (Default: none)")]
        public string UserAgent { get; set; } = "";

        //A header value has to be printable ASCII on one line. .NET sends a line break as it is, which would split
        //the header in two, and refuses to send any request with a character beyond ASCII in its headers.
        public bool UserAgentIsValid() => UserAgent.All(c => c is >= ' ' and <= '~');
    }
}
