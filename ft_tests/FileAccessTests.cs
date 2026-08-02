using ft.IO.Files;

namespace ft_tests
{
    /// <summary>
    /// Hermetic tests for <see cref="IFileAccess.ReadBytes"/> - the ranged read used by
    /// <see cref="ft.Listeners.UploadDownload"/> to fetch a file's 16-byte header without downloading the
    /// whole file. On the single-subfile transports (FTP/WebDAV/S3/Dropbox) that header shares a file with
    /// the entire command payload, so the distinction is the difference between 16 bytes and a full object
    /// fetch every few seconds.
    ///
    /// Two implementations must agree: <see cref="LocalAccess"/>, which seeks and reads natively, and the
    /// default interface implementation, which reads the whole file and slices (what a backend inherits
    /// when its transport has no ranged-read support). Both are exercised here against the same cases, so
    /// a backend that falls back is guaranteed to return identical bytes - just less cheaply.
    /// </summary>
    [TestClass]
    [TestCategory("Unit")]
    public class FileAccessTests
    {
        // Deliberately does NOT override ReadBytes, so calls land on the interface's default
        // slice-the-whole-file implementation. Everything else delegates to a real LocalAccess.
        sealed class FallbackOnlyAccess(LocalAccess inner) : IFileAccess
        {
            public bool Exists(string path) => inner.Exists(path);
            public void Delete(string path) => inner.Delete(path);
            public void WriteAllBytes(string path, ReadOnlyMemory<byte> bytes, bool overwrite = true) => inner.WriteAllBytes(path, bytes, overwrite);
            public void Move(string source, string dest, bool overwrite) => inner.Move(source, dest, overwrite);
            public byte[] ReadAllBytes(string path) => inner.ReadAllBytes(path);
            public long GetFileSize(string path) => inner.GetFileSize(path);
        }

        static IEnumerable<object[]> BothImplementations()
        {
            var local = new LocalAccess();
            yield return [local, "LocalAccess (native seek+read)"];
            yield return [new FallbackOnlyAccess(local), "default interface fallback (read-all + slice)"];
        }

        static string WriteTempFile(byte[] content)
        {
            var path = Path.GetTempFileName();
            File.WriteAllBytes(path, content);
            return path;
        }

        [DataTestMethod]
        [DynamicData(nameof(BothImplementations), DynamicDataSourceType.Method)]
        public void ReadBytes_ReadsRequestedSlice(IFileAccess access, string description)
        {
            var content = Enumerable.Range(0, 100).Select(i => (byte)i).ToArray();
            var path = WriteTempFile(content);

            try
            {
                CollectionAssert.AreEqual(content[..16], access.ReadBytes(path, 0, 16), $"{description}: header slice");
                CollectionAssert.AreEqual(content[10..18], access.ReadBytes(path, 10, 8), $"{description}: mid-file slice");
                CollectionAssert.AreEqual(content, access.ReadBytes(path, 0, content.Length), $"{description}: whole file");
            }
            finally
            {
                File.Delete(path);
            }
        }

        [DataTestMethod]
        [DynamicData(nameof(BothImplementations), DynamicDataSourceType.Method)]
        public void ReadBytes_ClampsToEndOfFile(IFileAccess access, string description)
        {
            // A file shorter than the header is exactly what the readers meet when the counterpart has
            // created a slot but not yet written it. Returning a short array (rather than throwing) lets
            // the caller's BinaryReader fail cleanly and be retried.
            var content = new byte[] { 1, 2, 3, 4 };
            var path = WriteTempFile(content);

            try
            {
                CollectionAssert.AreEqual(content, access.ReadBytes(path, 0, 16), $"{description}: count past EOF is clamped");
                CollectionAssert.AreEqual(new byte[] { 3, 4 }, access.ReadBytes(path, 2, 16), $"{description}: offset+count past EOF");
                Assert.AreEqual(0, access.ReadBytes(path, 4, 16).Length, $"{description}: offset at EOF returns empty");
                Assert.AreEqual(0, access.ReadBytes(path, 99, 16).Length, $"{description}: offset past EOF returns empty");
            }
            finally
            {
                File.Delete(path);
            }
        }

        [DataTestMethod]
        [DynamicData(nameof(BothImplementations), DynamicDataSourceType.Method)]
        public void ReadBytes_EmptyFileReturnsEmpty(IFileAccess access, string description)
        {
            var path = WriteTempFile([]);

            try
            {
                Assert.AreEqual(0, access.ReadBytes(path, 0, 16).Length, $"{description}: empty file");
            }
            finally
            {
                File.Delete(path);
            }
        }

        [TestMethod]
        public void ReadBytes_NativeAndFallbackAgree()
        {
            // The contract that matters: whichever path a backend takes, the bytes are identical.
            var local = new LocalAccess();
            var fallback = new FallbackOnlyAccess(local);

            var content = Enumerable.Range(0, 64).Select(i => (byte)(i * 7)).ToArray();
            var path = WriteTempFile(content);

            try
            {
                foreach (var (offset, count) in new[] { (0L, 8), (0L, 16), (8L, 8), (60L, 16), (64L, 8), (100L, 8) })
                {
                    CollectionAssert.AreEqual(
                        local.ReadBytes(path, offset, count),
                        ((IFileAccess)fallback).ReadBytes(path, offset, count),
                        $"native vs fallback disagree at offset {offset}, count {count}");
                }
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}
