using CommandLine;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ft.CLI
{
    public class S3Options : Options
    {
        [Option("s3", Required = false, HelpText = @"Use an S3 (or S3-compatible) bucket for the file tunnel. Connects directly to the bucket - no mounted file share required.")]
        public bool S3 { get; set; } = false;

        [Option("bucket", Required = true, HelpText = @"The S3 bucket name.")]
        public string Bucket { get; set; } = "";
        [Option("region", Required = false, HelpText = @"The AWS region of the bucket. Example: --region us-east-1 (Default us-east-1, or the AWS_DEFAULT_REGION / AWS_REGION environment variable)")]
        public string Region { get; set; } = "";

        [Option("endpoint", Required = false, HelpText = @"The S3 endpoint URL. Omit for AWS S3 (derived from --region), or specify for S3-compatible services. Example: --endpoint https://minio.example.com")]
        public string Endpoint { get; set; } = "";

        [Option("access-key", Required = false, HelpText = @"The S3 access key ID. Alternatively set the FT_S3_ACCESS_KEY environment variable to keep it out of the command line and process list.")]
        public string AccessKey { get; set; } = "";

        [Option("secret-key", Required = false, HelpText = @"The S3 secret access key. Alternatively set the FT_S3_SECRET_KEY environment variable to keep it out of the command line and process list.")]
        public string SecretKey { get; set; } = "";

        [Option("session-token", Required = false, HelpText = @"The S3 session token, required for temporary (STS) credentials (access keys beginning ""ASIA""). Alternatively set the FT_S3_SESSION_TOKEN or standard AWS_SESSION_TOKEN environment variable.")]
        public string SessionToken { get; set; } = "";

        [Option("max-connections", Required = false, HelpText = @"The maximum number of concurrent HTTP connections to the S3 endpoint. Default 20")]
        public int MaxConnections { get; set; } = 20;

        [Option('m', "max-size", Required = false, HelpText = @"The maximum size (in bytes) the file can be before uploading. Default 102400 (100 KB)")]
        public int MaxFileSizeBytes { get; set; } = 100 * 1024;

        public string ResolveAccessKey() => ResolveWithEnv(AccessKey, "FT_S3_ACCESS_KEY", "AWS_ACCESS_KEY_ID");

        public string ResolveSecretKey() => ResolveWithEnv(SecretKey, "FT_S3_SECRET_KEY", "AWS_SECRET_ACCESS_KEY");

        public string ResolveSessionToken() => ResolveWithEnv(SessionToken, "FT_S3_SESSION_TOKEN", "AWS_SESSION_TOKEN");

        //Explicit --region wins; otherwise the standard AWS env vars; otherwise the AWS default region.
        public string ResolveRegion()
        {
            var resolved = ResolveWithEnv(Region, "AWS_DEFAULT_REGION", "AWS_REGION");
            return string.IsNullOrWhiteSpace(resolved) ? "us-east-1" : resolved;
        }
    }
}
