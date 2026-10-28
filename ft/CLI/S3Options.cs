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
        [Option("region", Required = false, HelpText = @"The AWS region of the bucket. Example: --region us-east-1 (Default us-east-1, or for AWS itself the AWS_REGION / AWS_DEFAULT_REGION environment variable)")]
        public string Region { get; set; } = "";

        [Option("endpoint", Required = false, HelpText = @"The S3 endpoint URL. Omit for AWS S3 (derived from --region), or specify for S3-compatible services. Example: --endpoint https://minio.example.com")]
        public string Endpoint { get; set; } = "";

        [Option("access-key", Required = false, HelpText = @"The S3 access key ID. Alternatively set the FT_S3_ACCESS_KEY environment variable to keep it out of the command line and process list. If neither gives a key, the standard AWS_ACCESS_KEY_ID, AWS_SECRET_ACCESS_KEY and AWS_SESSION_TOKEN variables are used.")]
        public string AccessKey { get; set; } = "";

        [Option("secret-key", Required = false, HelpText = @"The S3 secret access key. Alternatively set the FT_S3_SECRET_KEY environment variable to keep it out of the command line and process list.")]
        public string SecretKey { get; set; } = "";

        [Option("session-token", Required = false, HelpText = @"The S3 session token, required for temporary (STS) credentials (on AWS, access keys beginning ""ASIA""). Alternatively set the FT_S3_SESSION_TOKEN environment variable, or AWS_SESSION_TOKEN when the keys come from the AWS_* variables.")]
        public string SessionToken { get; set; } = "";

        [Option("max-connections", Required = false, HelpText = @"The maximum number of concurrent HTTP connections to the S3 endpoint. Default 20")]
        public int MaxConnections { get; set; } = 20;

        [Option('m', "max-size", Required = false, HelpText = @"The maximum size (in bytes) the file can be before uploading. Default 102400 (100 KB)")]
        public int MaxFileSizeBytes { get; set; } = 100 * 1024;

        public (string AccessKey, string SecretKey, string SessionToken) ResolveCredentials() => ResolveCredentials(Environment.GetEnvironmentVariable);

        //The access key, secret key and session token, as one set. ft's own settings come first: each option,
        //or failing that its FT_S3_* variable. Only when they give no key at all are the standard AWS_*
        //variables used, and then all three together. A session token belongs to one temporary key, so taking
        //it from somewhere else than the keys - such as an AWS_SESSION_TOKEN left in the shell by an earlier
        //AWS session - would make every request fail.
        public (string AccessKey, string SecretKey, string SessionToken) ResolveCredentials(Func<string, string?> environment)
        {
            string Resolve(string value, string variable) => !string.IsNullOrEmpty(value) ? value : environment(variable) ?? "";

            var accessKey = Resolve(AccessKey, "FT_S3_ACCESS_KEY");
            var secretKey = Resolve(SecretKey, "FT_S3_SECRET_KEY");

            if (accessKey.Length == 0 && secretKey.Length == 0)
            {
                return (environment("AWS_ACCESS_KEY_ID") ?? "", environment("AWS_SECRET_ACCESS_KEY") ?? "", Resolve(SessionToken, "AWS_SESSION_TOKEN"));
            }

            return (accessKey, secretKey, Resolve(SessionToken, "FT_S3_SESSION_TOKEN"));
        }

        public string ResolveRegion() => ResolveRegion(Environment.GetEnvironmentVariable);

        //--region if given. For AWS itself (no --endpoint), otherwise the standard AWS_REGION, then
        //AWS_DEFAULT_REGION, the order the AWS CLI checks them in. They're set for AWS, so an S3-compatible
        //service doesn't use them: Cloudflare R2, for one, refuses any region but auto or us-east-1.
        public string ResolveRegion(Func<string, string?> environment)
        {
            var region = Region;

            if (string.IsNullOrWhiteSpace(region) && string.IsNullOrWhiteSpace(Endpoint))
            {
                region = environment("AWS_REGION");

                if (string.IsNullOrWhiteSpace(region))
                {
                    region = environment("AWS_DEFAULT_REGION");
                }
            }

            return string.IsNullOrWhiteSpace(region) ? "us-east-1" : region;
        }

        //AWS gives temporary credentials access keys beginning "ASIA", and they only work with their session
        //token. Other services choose their own key formats, so the check is for AWS itself only.
        public bool IsAwsTemporaryKey(string accessKey) =>
            string.IsNullOrWhiteSpace(Endpoint) && accessKey.StartsWith("ASIA", StringComparison.Ordinal);
    }
}
