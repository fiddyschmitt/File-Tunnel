using ft.CLI;

namespace ft_tests
{
    /// <summary>
    /// Where the S3 backend takes its credentials and region from. Hermetic: the environment is a
    /// dictionary handed to the resolver, not the test host's own.
    /// </summary>
    [TestClass]
    [TestCategory("Unit")]
    public class S3OptionsTests
    {
        static Func<string, string?> EnvironmentWith(params (string Name, string Value)[] variables)
        {
            var values = variables.ToDictionary(v => v.Name, v => v.Value);
            return name => values.TryGetValue(name, out var value) ? value : null;
        }

        static readonly Func<string, string?> AwsSession = EnvironmentWith(
            ("AWS_ACCESS_KEY_ID", "ASIAAWSKEY"),
            ("AWS_SECRET_ACCESS_KEY", "aws-secret"),
            ("AWS_SESSION_TOKEN", "aws-token"));

        [TestMethod]
        public void ResolveCredentials_CommandLineKeys_IgnoreTheAwsVariables()
        {
            var options = new S3Options { AccessKey = "AKIAEXAMPLE", SecretKey = "secret" };

            Assert.AreEqual(("AKIAEXAMPLE", "secret", ""), options.ResolveCredentials(AwsSession));
        }

        [TestMethod]
        public void ResolveCredentials_FtVariables_IgnoreTheAwsVariables()
        {
            var environment = EnvironmentWith(
                ("FT_S3_ACCESS_KEY", "minio-key"),
                ("FT_S3_SECRET_KEY", "minio-secret"),
                ("AWS_ACCESS_KEY_ID", "ASIAAWSKEY"),
                ("AWS_SECRET_ACCESS_KEY", "aws-secret"),
                ("AWS_SESSION_TOKEN", "aws-token"));

            Assert.AreEqual(("minio-key", "minio-secret", ""), new S3Options().ResolveCredentials(environment));
        }

        [TestMethod]
        public void ResolveCredentials_FtVariables_TakeTheFtSessionToken()
        {
            var environment = EnvironmentWith(
                ("FT_S3_ACCESS_KEY", "ASIAFTKEY"),
                ("FT_S3_SECRET_KEY", "ft-secret"),
                ("FT_S3_SESSION_TOKEN", "ft-token"),
                ("AWS_SESSION_TOKEN", "aws-token"));

            Assert.AreEqual(("ASIAFTKEY", "ft-secret", "ft-token"), new S3Options().ResolveCredentials(environment));
        }

        // As in v3.5.5: the key ID on the command line, the secret kept out of the process list.
        [TestMethod]
        public void ResolveCredentials_CommandLineKey_WithTheSecretInItsFtVariable()
        {
            var options = new S3Options { AccessKey = "AKIAEXAMPLE" };

            Assert.AreEqual(("AKIAEXAMPLE", "ft-secret", ""), options.ResolveCredentials(EnvironmentWith(("FT_S3_SECRET_KEY", "ft-secret"))));
        }

        [TestMethod]
        public void ResolveCredentials_CommandLineKey_DoesNotBorrowTheAwsSecret()
        {
            var options = new S3Options { AccessKey = "AKIAEXAMPLE" };

            Assert.AreEqual(("AKIAEXAMPLE", "", ""), options.ResolveCredentials(AwsSession));
        }

        [TestMethod]
        public void ResolveCredentials_NoKeysOfItsOwn_TakesTheAwsVariablesTogether()
        {
            Assert.AreEqual(("ASIAAWSKEY", "aws-secret", "aws-token"), new S3Options().ResolveCredentials(AwsSession));
        }

        [TestMethod]
        public void ResolveCredentials_CommandLineToken_GoesWithTheAwsKeys()
        {
            var options = new S3Options { SessionToken = "typed-token" };

            Assert.AreEqual(("ASIAAWSKEY", "aws-secret", "typed-token"), options.ResolveCredentials(AwsSession));
        }

        [TestMethod]
        public void ResolveCredentials_NothingSet_IsEmpty()
        {
            Assert.AreEqual(("", "", ""), new S3Options().ResolveCredentials(EnvironmentWith()));
        }

        [TestMethod]
        [DataRow("", "", "", "", "us-east-1", "nothing set")]
        [DataRow("eu-west-2", "", "eu-west-1", "ap-southeast-2", "eu-west-2", "--region wins")]
        [DataRow("", "", "eu-west-1", "", "eu-west-1", "AWS_REGION")]
        [DataRow("", "", "", "ap-southeast-2", "ap-southeast-2", "AWS_DEFAULT_REGION")]
        [DataRow("", "", "eu-west-1", "ap-southeast-2", "eu-west-1", "AWS_REGION before AWS_DEFAULT_REGION, as the AWS CLI does")]
        [DataRow("", "https://account.r2.cloudflarestorage.com", "eu-west-1", "ap-southeast-2", "us-east-1", "the AWS variables don't apply to another service")]
        [DataRow("auto", "https://account.r2.cloudflarestorage.com", "eu-west-1", "", "auto", "--region still applies to another service")]
        public void ResolveRegion(string region, string endpoint, string awsRegion, string awsDefaultRegion, string expected, string description)
        {
            var options = new S3Options { Region = region, Endpoint = endpoint };
            var environment = EnvironmentWith(("AWS_REGION", awsRegion), ("AWS_DEFAULT_REGION", awsDefaultRegion));

            Assert.AreEqual(expected, options.ResolveRegion(environment), description);
        }

        [TestMethod]
        [DataRow("ASIAEXAMPLE", "", true, "a temporary AWS key")]
        [DataRow("AKIAEXAMPLE", "", false, "a long-lived AWS key")]
        [DataRow("asia-backup", "", false, "AWS key IDs are upper case")]
        [DataRow("ASIA-OFFICE", "https://minio.example.com", false, "another service chooses its own keys")]
        public void IsAwsTemporaryKey(string accessKey, string endpoint, bool expected, string description)
        {
            Assert.AreEqual(expected, new S3Options { Endpoint = endpoint }.IsAwsTemporaryKey(accessKey), description);
        }
    }
}
