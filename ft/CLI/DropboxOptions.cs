using CommandLine;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ft.CLI
{
    public class DropboxOptions : Options
    {
        [Option("dropbox", Required = false, HelpText = @"Use a Dropbox account for the file tunnel. Connects to Dropbox directly and does not require a mounted file share.")]
        public bool Dropbox { get; set; } = false;

        [Option("app-key", Required = false, HelpText = @"The Dropbox app key. Alternatively set the FT_DROPBOX_APP_KEY environment variable.")]
        public string AppKey { get; set; } = "";

        [Option("app-secret", Required = false, HelpText = @"The Dropbox app secret. Alternatively set the FT_DROPBOX_APP_SECRET environment variable to keep it out of the command line and process list.")]
        public string AppSecret { get; set; } = "";

        [Option("refresh-token", Required = false, HelpText = @"The Dropbox OAuth2 refresh token, obtained once via the authorize flow (see the wiki). Alternatively set the FT_DROPBOX_REFRESH_TOKEN environment variable to keep it out of the command line and process list.")]
        public string RefreshToken { get; set; } = "";

        [Option('m', "max-size", Required = false, HelpText = @"The maximum size (in bytes) the file can be before uploading. Default 102400 (100 KB)")]
        public int MaxFileSizeBytes { get; set; } = 100 * 1024;

        public string ResolveAppKey() => ResolveWithEnv(AppKey, "FT_DROPBOX_APP_KEY");

        public string ResolveAppSecret() => ResolveWithEnv(AppSecret, "FT_DROPBOX_APP_SECRET");

        public string ResolveRefreshToken() => ResolveWithEnv(RefreshToken, "FT_DROPBOX_REFRESH_TOKEN");
    }
}
