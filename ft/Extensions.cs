using ft.CLI;
using ft.Commands;
using ft.Streams;
using ft.Utilities;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.RateLimiting;
using System.Threading.Tasks;

namespace ft
{
    public static class Extensions
    {
        /*
        public static void CopyTo(this Stream input, Stream output, int bufferSize, Action<int> callBack, CancellationTokenSource cancellationTokenSource)
        {
            var buffer = new byte[bufferSize];

            int read;
            while ((read = input.Read(buffer, 0, bufferSize)) > 0)
            {
                if (cancellationTokenSource != null && cancellationTokenSource.IsCancellationRequested)
                {
                    break;
                }

                output.Write(buffer, 0, read);

                callBack?.Invoke(read);
            }
            callBack?.Invoke(read);
        }
        */

        public const int ARBITARY_MEDIUM_SIZE_BUFFER = 5 * 1024 * 1024;

        //Initialised to something big, because otherwise it defaults to 1MB and smaller.
        //See: https://adamsitnik.com/Array-Pool/
        //Always remember to return the array back into the pool.
        //Never trust buffer.Length
        public static readonly ArrayPool<byte> BufferPool = ArrayPool<byte>.Create(ARBITARY_MEDIUM_SIZE_BUFFER + 1, 50);

        public static void CopyTo(this Stream input, Stream output, int bufferSize, Action<int> callBack, CancellationTokenSource? cancellationTokenSource, int readDurationMillis)
        {
            var buffer = BufferPool.Rent(bufferSize);

            //optimisation to get good responsiveness, and good bandwidth when there's lots of incoming data
            var maxQuietDurationMillis = (int)Math.Max(1, readDurationMillis / 4d);

            var read = 0;
            while (true)
            {
                if (cancellationTokenSource != null && cancellationTokenSource.IsCancellationRequested)
                {
                    break;
                }

                if (input is not NetworkStream inputNetworkStream || readDurationMillis <= 0)
                {
                    read = input.Read(buffer, 0, bufferSize);
                }
                else
                {
                    //Speed optimisation.
                    //We want to avoid writing tiny amounts of data to file, because IO is expensive. Let's accumulate n milliseconds worth of data.
                    read = inputNetworkStream.Read(buffer, 0, bufferSize, readDurationMillis, maxQuietDurationMillis);
                }

                if (read == 0)
                {
                    break;
                }

                output.Write(buffer, 0, read);

                callBack?.Invoke(read);
            }
            callBack?.Invoke(read);

            BufferPool.Return(buffer);
        }

        public static string GetName(this Command command)
        {
            string result;
            if (command is Ping p)
            {
                if (p.PingType == EnumPingType.Request)
                {
                    result = $"Ping request";
                }
                else
                {
                    result = $"Ping response for {p.ResponseToPacketNumber}";
                }
            }
            else
            {
                result = $"{command.GetType().Name}";
            }

            return result;
        }

        public static string GetMD5(this byte[] data)
        {
            var stream = new MemoryStream(data);
            var result = stream.GetMD5();
            return result;
        }

        public static string GetMD5(this Stream stream)
        {
            stream.Seek(0, SeekOrigin.Begin);

            using var md5Instance = System.Security.Cryptography.MD5.Create();
            var hashResult = md5Instance.ComputeHash(stream);

            var result = Convert.ToHexStringLower(hashResult);
            return result;
        }

        public static int Read(this NetworkStream input, byte[] buffer, int offset, int count, int maxDurationMillis, int maxQuietDurationMillis)
        {
            var totalTime = new Stopwatch();
            var timeSinceLastRead = new Stopwatch();

            var totalBytesRead = 0;
            var currentOffset = offset;

            while (true)
            {
                if (input.DataAvailable)
                {
                    if (!totalTime.IsRunning)
                    {
                        totalTime.Start();
                    }

                    var toRead = Math.Min(count - totalBytesRead, buffer.Length - currentOffset);

                    if (toRead <= 0)
                    {
                        break;
                    }

                    var bytesRead = input.Read(buffer, currentOffset, toRead);

                    timeSinceLastRead.Restart();

                    currentOffset += bytesRead;
                    totalBytesRead += bytesRead;
                }
                else
                {
                    Delay.Wait(1);
                }

                if (totalBytesRead > 0 && timeSinceLastRead.IsRunning && timeSinceLastRead.ElapsedMilliseconds > maxQuietDurationMillis)
                {
                    break;
                }

                if (totalBytesRead > 0 && totalTime.IsRunning && totalTime.ElapsedMilliseconds > maxDurationMillis)
                {
                    break;
                }

                if (!input.Socket.SocketConnected())
                {
                    break;
                }
            }

            totalTime.Stop();

            return totalBytesRead;
        }

        public static bool SocketConnected(this Socket s)
        {
            var part1 = s.Poll(1000, SelectMode.SelectRead);
            var part2 = s.Available == 0;
            if (part1 && part2)
            {
                return false;
            }
            else
            {
                return true;
            }
        }

        static readonly Dictionary<Stream, (string ReadString, string WriteString)> StreamNames = [];
        public static string Name(this Stream stream, bool readFrom)
        {
            try
            {
                if (!StreamNames.ContainsKey(stream))
                {
                    if (stream is UdpStream udpStream)
                    {
                        StreamNames.Add(
                                stream,
                                ($"{udpStream.SendTo} -> {udpStream.Client.Client.LocalEndPoint}",
                                $"{udpStream.Client.Client.LocalEndPoint} -> {udpStream.SendTo}"));
                    }

                    if (stream is NetworkStream networkStream)
                    {
                        StreamNames.Add(
                                stream,
                                ($"{networkStream.Socket.RemoteEndPoint} -> {networkStream.Socket.LocalEndPoint}",
                                 $"{networkStream.Socket.LocalEndPoint} -> {networkStream.Socket.RemoteEndPoint}"));
                    }


                    if (stream is SharedFileStream sharedFileStream)
                    {
                        StreamNames.Add(
                                stream,
                                (Path.GetFileName(sharedFileStream.SharedFileManager.ReadFromFilename),
                                 Path.GetFileName(sharedFileStream.SharedFileManager.WriteToFilename)));
                    }
                }

                var (ReadString, WriteString) = StreamNames[stream];

                var streamName = readFrom ? ReadString : WriteString;

                return streamName;
            }
            catch (Exception)
            {
                return "Unknown stream";
            }
        }

        public static string BytesToString(this uint bytes)
        {
            var result = BytesToString((ulong)bytes);
            return result;
        }

        public static string BytesToString(this int bytes)
        {
            var result = BytesToString((ulong)bytes);
            return result;
        }

        public static string BytesToString(this long bytes)
        {
            var result = BytesToString((ulong)bytes);
            return result;
        }

        public static string BytesToString(this ulong bytes)
        {
            string[] UNITS = ["B", "KB", "MB", "GB", "TB", "PB", "EB"];
            int c;
            for (c = 0; c < UNITS.Length; c++)
            {
                ulong m = (ulong)1 << ((c + 1) * 10);
                if (bytes < m)
                    break;
            }

            double n = bytes / (double)((ulong)1 << (c * 10));
            return string.Format("{0:0.##} {1}", n, UNITS[c]);
        }

        public static bool IsValidEndpoint(this string endpointStr)
        {
            var result = false;

            if (!string.IsNullOrEmpty(endpointStr))
            {
                if (IPEndPoint.TryParse(endpointStr, out var ep))
                {
                    if (ep.Port > 0)
                    {
                        result = true;
                    }
                }
                else
                {
                    var tokens = endpointStr.Split([":"], StringSplitOptions.None);
                    if (tokens.Length == 2 && int.TryParse(tokens[1], out var _))
                    {
                        result = true;
                    }
                }
            }

            return result;
        }

        public static string ToString(this IEnumerable<string> list, string seperator)
        {
            var result = string.Join(seperator, list);
            return result;
        }

        public static bool IsIPV6(this string ipStr)
        {
            var result = IPAddress.TryParse(ipStr, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6;
            return result;
        }

        public static string WrapIfIPV6(this string ipStr)
        {
            var result = ipStr.IsIPV6() ? $"[{ipStr}]" : ipStr;
            return result;
        }

        public static IPEndPoint ToIpEndpoint(this EndPoint endPoint)
        {
            if (endPoint is IPEndPoint ipEndpoint)
            {
                return ipEndpoint;
            }

            if (endPoint is DnsEndPoint dnsEndPoint)
            {
                var addresses = Dns.GetHostAddresses(dnsEndPoint.Host);
                if (addresses == null || addresses.Length == 0)
                {
                    throw new Exception($"Unable to retrieve IP address from specified host name: {dnsEndPoint.Host}");
                }

                return new IPEndPoint(addresses[0], dnsEndPoint.Port);
            }

            throw new Exception($"Unhandled Endpoint type: {endPoint.GetType()}");
        }

        public static IPEndPoint AsEndpoint(this string endpointStr)
        {
            var endpoint = NetworkUtilities.ParseEndpoint(endpointStr);
            var result = endpoint.ToIpEndpoint();
            return result;
        }

        public static (int Attempts, TimeSpan Duration) Time(
            string operation,
            Func<(int Attempt, TimeSpan Elapsed), bool> action,
            Func<(int Attempt, TimeSpan Elapsed, string Operation), int> getSleepDurationMillis,
            bool printOutput)
        {
            printOutput &= Debugger.IsAttached;


            if (printOutput)
            {
                Program.Log($"Started {operation}");
            }

            var sw = new Stopwatch();
            sw.Start();

            var attempt = 1;
            while (true)
            {
                if (printOutput)
                {
                    Program.Log($"{operation} attempt {attempt:N0}");
                }

                var finished = action((attempt, sw.Elapsed));

                if (finished)
                {
                    break;
                }

                var sleepMillis = getSleepDurationMillis((attempt, sw.Elapsed, operation));

                Delay.Wait(sleepMillis);

                attempt++;
            }

            sw.Stop();

            if (printOutput && sw.ElapsedMilliseconds > 1000)
            {
                Program.Log($"{operation} took {attempt:N0} attempts ({sw.Elapsed.TotalSeconds:N3} seconds)");
            }

            if (printOutput)
            {
                Program.Log($"{operation} took {attempt:N0} attempts ({sw.Elapsed.TotalSeconds:N3} seconds)");
            }

            return (attempt, sw.Elapsed);
        }

        public static void Retry(string operation, Action action, bool verbose, int timeoutMilliseconds)
        {
            Time(
                operation,
                (attempt) =>
                {
                    try
                    {
                        action();
                    }
                    catch
                    {
                        return false;
                    }

                    return true;
                },
                (attempt) =>
                {
                    if (attempt.Elapsed.TotalMilliseconds > timeoutMilliseconds)
                    {
                        throw new Exception($"Timeout during {attempt.Operation}");
                    }

                    return 10;
                },
                verbose);
        }

        public static void Flush(this Stream stream, bool verbose, int timeoutMilliseconds)
        {
            if (Options.Citrix && stream is FileStream fileStream)
            {
                Retry($"Flush to disk", () => fileStream.Flush(true), verbose, timeoutMilliseconds);
            }
            else
            {
                Retry($"{nameof(stream)}.{nameof(Stream.Flush)}", stream.Flush, verbose, timeoutMilliseconds);
            }
        }

        public static void Flush(this BinaryWriter binaryWriter, bool verbose, int timeoutMilliseconds)
        {
            Retry($"{nameof(BinaryWriter)}.{nameof(BinaryWriter.Flush)}", binaryWriter.Flush, verbose, timeoutMilliseconds);

            if (binaryWriter.BaseStream != null)
            {
                Flush(binaryWriter.BaseStream, verbose, timeoutMilliseconds);
            }
        }

        [DllImport("libc", SetLastError = true, EntryPoint = "open")]
        private static extern int libc_open([MarshalAs(UnmanagedType.LPStr)] string pathname, int flags);
        [DllImport("libc", SetLastError = true, EntryPoint = "read")]
        private static extern long libc_read(int fd, IntPtr buf, ulong count);
        [DllImport("libc", SetLastError = true, EntryPoint = "lseek")]
        private static extern long libc_lseek(int fd, long offset, int whence);
        [DllImport("libc", SetLastError = true, EntryPoint = "close")]
        private static extern int libc_close(int fd);
        [DllImport("libc", SetLastError = true, EntryPoint = "statfs")]
        private static extern int statfs(string path, byte[] buf);
        private const int O_RDONLY = 0;
        private const int SEEK_SET = 0;
        private const int DIRECT_ALIGN = 4096; // O_DIRECT buffer/offset/length alignment
        private const int FUSE_SUPER_MAGIC = 0x65735546;   // statfs f_type for FUSE mounts (sshfs)
        private const int VBOXSF_SUPER_MAGIC = 0x786F4256; // statfs f_type for VirtualBox shared folders
        private const int V9FS_SUPER_MAGIC = 0x01021997;   // statfs f_type for 9P (Plan 9 / diod) mounts

        // The kind of filesystem a mount is, derived purely from its statfs f_type magic. Pure and
        // side-effect-free so it is unit-testable without a real mount. The same magic covers a whole
        // transport family: V9FS is BOTH 9P-over-TCP (diod) AND virtio-9p; FUSE is BOTH sshfs AND
        // virtio-fs - so ft's auto-detection covers the virtio variants with no extra magic.
        public enum MountKind { Other, NineP, Fuse, Vboxsf }

        public static MountKind ClassifyMountMagic(int magic) => magic switch
        {
            V9FS_SUPER_MAGIC => MountKind.NineP,
            FUSE_SUPER_MAGIC => MountKind.Fuse,
            VBOXSF_SUPER_MAGIC => MountKind.Vboxsf,
            _ => MountKind.Other,
        };

        // The mount's statfs f_type spots filesystems that serve a stale view to a held read handle
        // (a mount can't change type, so cache it per path). FUSE/sshfs and vboxsf both do, and only
        // refresh on a fresh open(), so they default to the reopen-per-read of --isolated-reads
        // (enabled in Program.cs). The O_DIRECT refresh below (TryDirectRefresh) is a retained in-place
        // alternative for a FUSE mount run in Normal mode - it keeps Normal-mode speed, but on sshfs
        // under load it can occasionally fail to recover a stale read before the tunnel-offline timeout,
        // which is why IsolatedReads is the default. CIFS/NFS need neither (an O_DIRECT round-trip on
        // CIFS starved the keepalive ping, regressing SMB-Linux Normal).
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> MountMagicCache = new();

        private static int MountMagic(string path)
        {
            return MountMagicCache.GetOrAdd(path, p =>
            {
                try
                {
                    // The read file may not exist yet (the counterpart creates it), so fall back to its
                    // directory — which is on the same mount and present from the start.
                    var target = File.Exists(p) ? p : (Path.GetDirectoryName(p) ?? p);
                    var buf = new byte[256]; // struct statfs is ~120 bytes; f_type magic is at offset 0
                    return statfs(target, buf) == 0 ? BitConverter.ToInt32(buf, 0) : 0;
                }
                catch { return 0; }
            });
        }

        // True when the path is on a VirtualBox shared folder (vboxsf) / an sshfs (FUSE) mount. Both
        // serve a stale view to a held read handle and only refresh on a fresh open(), so they default
        // to the reopen-per-read of --isolated-reads (enabled in Program.cs).
        public static bool IsVboxsfMount(string path) => ClassifyMountMagic(MountMagic(path)) == MountKind.Vboxsf;
        public static bool IsFuseMount(string path) => ClassifyMountMagic(MountMagic(path)) == MountKind.Fuse;

        // True when the path is on a 9P (Plan 9 protocol, e.g. diod) mount. 9P is cache-coherent but
        // delivers whole files out of order and can be caught mid-write at large sizes; used to apply the
        // upload-download small-file cap + low pace automatically, keyed off the mount rather than a flag.
        public static bool IsNinePMount(string path) => ClassifyMountMagic(MountMagic(path)) == MountKind.NineP;

        // statfs reports plain FUSE for ALL fuse mounts, so it can't separate sshfs (held handle stays
        // stale / Normal is unreliable -> IsolatedReads) from virtio-fs (held handle refreshes on an fstat,
        // measured ~2.4x faster than reopen -> Normal). The mount's fstype string does: "virtiofs" vs
        // "fuse.sshfs". Read it from /proc/self/mountinfo (the longest mountpoint that prefixes the path).
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> MountFsTypeCache = new();

        private static string MountFsType(string path)
        {
            return MountFsTypeCache.GetOrAdd(path, p =>
            {
                try
                {
                    var full = Path.GetFullPath(File.Exists(p) ? p : (Path.GetDirectoryName(p) ?? p));
                    var bestMount = ""; var bestFs = "";
                    foreach (var line in File.ReadAllLines("/proc/self/mountinfo"))
                    {
                        // <id> <parent> <maj:min> <root> <mountpoint> <opts> [tags] - <fstype> <source> <superopts>
                        var dash = line.IndexOf(" - ", StringComparison.Ordinal);
                        if (dash < 0) continue;
                        var left = line[..dash].Split(' ');
                        if (left.Length < 5) continue;
                        var mountpoint = left[4];
                        var fstype = line[(dash + 3)..].Split(' ')[0];
                        var isMatch = full == mountpoint || full.StartsWith(mountpoint.TrimEnd('/') + "/", StringComparison.Ordinal);
                        if (isMatch && mountpoint.Length > bestMount.Length)
                        {
                            bestMount = mountpoint; bestFs = fstype;
                        }
                    }
                    return bestFs;
                }
                catch { return ""; }
            });
        }

        // True when the path is on a virtio-fs mount (a FUSE subtype). Unlike sshfs, its held read handle
        // refreshes via fstat (ForceRead does this), so it runs Normal mode - the fastest working mode here.
        public static bool IsVirtioFsMount(string path) =>
            IsFuseMount(path) && string.Equals(MountFsType(path), "virtiofs", StringComparison.Ordinal);

        // The read strategies a tunnel can use, fastest first.
        public enum TunnelMode { Normal, IsolatedReads, UploadDownload }

        public static string ModeFlag(this TunnelMode mode) => mode switch
        {
            TunnelMode.IsolatedReads => "--isolated-reads",
            TunnelMode.UploadDownload => "--upload-download",
            _ => "--normal",
        };

        // Which tunnel read modes are known to work on a filesystem, and which ft prefers (the fastest that
        // works). Supports() reports whether a user-requested mode is one of the known-good ones.
        public readonly record struct FileSystemModes(string Description, TunnelMode Preferred, TunnelMode[] KnownGood)
        {
            public bool Supports(TunnelMode mode) => Array.IndexOf(KnownGood, mode) >= 0;
        }

        private static readonly TunnelMode[] AllModes = [TunnelMode.Normal, TunnelMode.IsolatedReads, TunnelMode.UploadDownload];
        private static readonly TunnelMode[] ReopenOnly = [TunnelMode.IsolatedReads, TunnelMode.UploadDownload];
        private static readonly TunnelMode[] UploadOnly = [TunnelMode.UploadDownload];

        // THE single source of truth for ft's per-filesystem read-mode knowledge: which modes work on the
        // filesystem the Read file is on, and which ft prefers (fastest that works). The auto-selector applies
        // Preferred when the user gives no mode; the conflict warning checks Preferred/Supports(). Detection is
        // by statfs magic, refined by mountinfo fstype where the magic is ambiguous (virtio-fs vs sshfs).
        // Description is empty for ordinary coherent filesystems, which need no announcement.
        public static FileSystemModes ModesForReadFile(string path) =>
            // virtio-fs: a held handle refreshes via fstat (ForceRead does it) -> Normal works, ~2.4x faster
            // than reopen-per-read. All three modes work.
            IsVirtioFsMount(path) ? new("a virtio-fs mount", TunnelMode.Normal, AllModes) :
            // vboxsf: a held handle stays stale even after dropping caches; only a fresh open() sees new data.
            IsVboxsfMount(path) ? new("a VirtualBox shared folder (vboxsf)", TunnelMode.IsolatedReads, ReopenOnly) :
            // sshfs / other FUSE: a held handle is stale and an in-place refresh is unreliable under load.
            IsFuseMount(path) ? new("an sshfs / FUSE mount", TunnelMode.IsolatedReads, ReopenOnly) :
            // 9P (diod over TCP): cache-coherent but delivers whole files out of order -> whole-file transfer only.
            IsNinePMount(path) ? new("a 9P mount", TunnelMode.UploadDownload, UploadOnly) :
            // Coherent local/network filesystems (ext4, NFS, CIFS, and QEMU virtio-9p, which reports its ext
            // backing magic): a held handle sees appends -> all modes work, Normal is fastest.
            new("", TunnelMode.Normal, AllModes);

        // O_DIRECT's numeric value is architecture-specific on Linux. Confirmed 0x4000 on x86/x86-64;
        // null on architectures we haven't validated, where ForceRead falls back to a plain read.
        private static readonly int? LinuxODirect = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 or Architecture.X86 => 0x4000,
            _ => null,
        };

        public static void ForceRead(this Stream stream, int tunnelTimeoutMilliseconds, bool verbose)
        {
            if (stream is FileStream fileStream)
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    // A FUSE mount can keep serving a stale, short view of a file another host is appending
                    // to, through a long-lived held read handle. Two complementary refreshes, both gated to
                    // FUSE inside the helpers (CIFS/NFS revalidate on read, and an extra round-trip there can
                    // starve the keepalive ping):
                    //  - fstat the HELD handle: re-fetches its cached inode size. virtio-fs needs this - a
                    //    plain read at EOF trusts the stale size, but an explicit fstat refreshes it, after
                    //    which the buffered reader reads past the old EOF.
                    //  - O_DIRECT read on a SEPARATE handle: forces a backing-store round-trip that refreshes
                    //    sshfs's inode cache for all handles. Falls back to a plain buffered read where
                    //    O_DIRECT is unavailable.
                    TryFstatRefresh(fileStream);
                    if (!TryDirectRefresh(fileStream))
                    {
                        try
                        {
                            using var tempFs = new FileStream(fileStream.Name, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                            _ = tempFs.Read(new byte[DIRECT_ALIGN]);
                        }
                        catch { }
                    }
                }
                else
                {
                    fileStream.Flush(verbose, tunnelTimeoutMilliseconds);
                }
            }
            else
            {
                stream.Flush(verbose, tunnelTimeoutMilliseconds);
            }
        }

        // Re-fetches the held handle's inode size with a single fstat, gated to FUSE mounts. virtio-fs
        // (and potentially other FUSE servers) keep a long-lived read handle's cached size stale - a plain
        // read at EOF returns 0 - until something explicitly stats the fd; this refreshes it without
        // reopening. CIFS/NFS are skipped (they revalidate on read; an extra round-trip can starve the ping).
        private static void TryFstatRefresh(FileStream fileStream)
        {
            if (MountMagic(fileStream.Name) != FUSE_SUPER_MAGIC)
            {
                return;
            }

            try
            {
                _ = RandomAccess.GetLength(fileStream.SafeFileHandle);
            }
            catch { }
        }

        // Reads the page the reader is waiting on through a separate O_DIRECT (unbuffered) handle, to
        // force a backing-store round-trip that refreshes the inode cache. Returns false (so the caller
        // can fall back) when O_DIRECT is unavailable for this architecture or the open fails.
        private static bool TryDirectRefresh(FileStream fileStream)
        {
            if (LinuxODirect is not int oDirect)
            {
                return false;
            }

            // Only worthwhile (and only safe) on FUSE mounts such as sshfs; CIFS/NFS fall back below.
            if (MountMagic(fileStream.Name) != FUSE_SUPER_MAGIC)
            {
                return false;
            }

            var fd = libc_open(fileStream.Name, O_RDONLY | oDirect);
            if (fd < 0)
            {
                return false;
            }

            var raw = IntPtr.Zero;
            try
            {
                raw = Marshal.AllocHGlobal(DIRECT_ALIGN * 2);
                var aligned = (IntPtr)(((long)raw + (DIRECT_ALIGN - 1)) & ~((long)DIRECT_ALIGN - 1));
                var alignedOffset = fileStream.Position & ~((long)DIRECT_ALIGN - 1);
                libc_lseek(fd, alignedOffset, SEEK_SET);
                _ = libc_read(fd, aligned, (ulong)DIRECT_ALIGN);
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (raw != IntPtr.Zero) Marshal.FreeHGlobal(raw);
                libc_close(fd);
            }
        }

        public static void Wait(this ReplenishingRateLimiter? limiter)
        {
            limiter?.AcquireAsync(1, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        }
    }
}
