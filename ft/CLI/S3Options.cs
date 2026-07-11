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
        [Option("s3-native", Required = false, HelpText = @"Use a native S3 (or S3-compatible) client for the file tunnel. Unlike --s3, this connects directly to the bucket and does not require a mounted file share.")]
        public bool S3Native { get; set; } = false;

        [Option("bucket", Required = true, HelpText = @"The S3 bucket name.")]
        public string Bucket { get; set; } = "";
        [Option("region", Required = false, HelpText = @"The AWS region of the bucket. Example: --region us-east-1 (Default us-east-1)")]
        public string Region { get; set; } = "us-east-1";

        [Option("endpoint", Required = false, HelpText = @"The S3 endpoint URL. Omit for AWS S3 (derived from --region), or specify for S3-compatible services. Example: --endpoint https://minio.example.com")]
        public string Endpoint { get; set; } = "";

        [Option("access-key", Required = true, HelpText = @"The S3 access key ID.")]
        public string AccessKey { get; set; } = "";

        [Option("secret-key", Required = true, HelpText = @"The S3 secret access key.")]
        public string SecretKey { get; set; } = "";

        [Option('m', "max-size", Required = false, HelpText = @"The maximum size (in bytes) the file can be before uploading. Default 102400 (100 KB)")]
        public int MaxFileSizeBytes { get; set; } = 100 * 1024;
    }
}
