using CommandLine;

namespace ft.CLI
{
    public class SqsOptions : Options
    {
        [Option("sqs", Required = false, HelpText = @"Use an SQS-compatible message queue service (e.g. Yandex Message Queue).")]
        public bool SQS { get; set; } = false;

        [Option("region", Required = false, HelpText = @"The AWS region (or Yandex region, e.g. ru-central1). Default us-east-1")]
        public string Region { get; set; } = "us-east-1";

        [Option("access-key", Required = false, HelpText = @"The SQS access key ID. Alternatively set the FT_SQS_ACCESS_KEY environment variable.")]
        public string AccessKey { get; set; } = "";

        [Option("secret-key", Required = false, HelpText = @"The SQS secret access key. Alternatively set the FT_SQS_SECRET_KEY environment variable.")]
        public string SecretKey { get; set; } = "";

        [Option("max-connections", Required = false, HelpText = @"The maximum number of concurrent HTTP connections to the SQS endpoint. Default 20")]
        public int MaxConnections { get; set; } = 20;

        [Option('m', "max-size", Required = false, HelpText = @"The threshold size (in bytes) before sending a message. Default 92160 (90 KB) to ensure Base64+URLEncode fits well under 256KB.")]
        public int MaxFileSizeBytes { get; set; } = 90 * 1024;

        public string ResolveAccessKey() => ResolveWithEnv(AccessKey, "FT_SQS_ACCESS_KEY");
        public string ResolveSecretKey() => ResolveWithEnv(SecretKey, "FT_SQS_SECRET_KEY");
    }
}
