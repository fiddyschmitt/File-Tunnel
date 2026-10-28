using ft.IO.Files;
using ft_tests.Utilities;

namespace ft_tests
{
    /// <summary>
    /// ft's S3 client against an in-memory bucket (<see cref="FakeS3Bucket"/>): how it frees a file when its
    /// credentials may read and write but not delete.
    /// </summary>
    [TestClass]
    [TestCategory("Unit")]
    public class S3AccessTests
    {
        static S3 Client(FakeS3Bucket bucket, string accessKey) =>
            new("http://s3.test", "us-east-1", "bucket", accessKey, "secret", messageHandler: bucket);

        [TestMethod]
        public void Delete_WhenAllowed_RemovesTheFile()
        {
            var bucket = new FakeS3Bucket();
            var s3 = Client(bucket, "admin");
            s3.WriteAllBytes("1.dat", new byte[] { 1, 2, 3 });

            s3.Delete("1.dat");

            Assert.IsFalse(bucket.Objects.ContainsKey("/bucket/1.dat"));
        }

        [TestMethod]
        public void Delete_WhenAccessDenied_EmptiesTheFileInstead()
        {
            var bucket = new FakeS3Bucket("participant");
            var s3 = Client(bucket, "participant");
            s3.WriteAllBytes("1.dat", new byte[] { 1, 2, 3 });

            s3.Delete("1.dat");

            Assert.AreEqual(0, bucket.Objects["/bucket/1.dat"].Length);
            Assert.IsFalse(s3.Exists("1.dat"));
        }

        [TestMethod]
        public void Delete_AfterAccessDenied_StopsAskingToDelete()
        {
            var bucket = new FakeS3Bucket("participant");
            var s3 = Client(bucket, "participant");

            s3.Delete("1.dat");
            s3.Delete("1.dat");
            s3.Delete("2.dat");

            Assert.AreEqual(1, bucket.DeleteRequests);
        }

        // Any other refusal, such as a skewed clock, fails every request alike: it's no sign of a missing permission.
        [TestMethod]
        public void Delete_RefusedForAnotherReason_Throws()
        {
            var bucket = new FakeS3Bucket("participant") { DeniedCode = "RequestTimeTooSkewed" };
            var s3 = Client(bucket, "participant");
            s3.WriteAllBytes("1.dat", new byte[] { 1, 2, 3 });

            Assert.ThrowsExactly<HttpRequestException>(() => s3.Delete("1.dat"));
            Assert.AreEqual(3, bucket.Objects["/bucket/1.dat"].Length);
        }

        // An empty file is one that an end without delete permission emptied, so it counts as gone. That holds
        // for both ends, including one that may delete: it's the other end that frees its files.
        [TestMethod]
        public void Exists_EmptyFile_CountsAsGone()
        {
            var bucket = new FakeS3Bucket();
            var s3 = Client(bucket, "admin");

            Assert.IsFalse(s3.Exists("1.dat"));

            s3.WriteAllBytes("1.dat", ReadOnlyMemory<byte>.Empty);
            Assert.IsFalse(s3.Exists("1.dat"));

            s3.WriteAllBytes("1.dat", new byte[] { 1 });
            Assert.IsTrue(s3.Exists("1.dat"));
        }
    }
}
