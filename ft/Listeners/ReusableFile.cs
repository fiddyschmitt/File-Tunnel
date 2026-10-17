using ft.Bandwidth;
using ft.CLI;
using ft.Commands;
using ft.IO;
using ft.Streams;
using ft.Utilities;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.RateLimiting;
using System.Threading.Tasks;

namespace ft.Listeners
{
    public class ReusableFile : SharedFileManager
    {
        //How long to keep re-reading a command that is only partially visible before giving up and
        //falling back to restarting the pump. Comfortably under the tunnel timeout, so a genuinely
        //corrupt read still surfaces long before the counterpart could be declared offline.
        const int ReadRetryMilliseconds = 2000;

        const long SESSION_ID = 0;
        const int READY_FOR_PURGE_FLAG = sizeof(long);
        const int PURGE_COMPLETE_FLAG = READY_FOR_PURGE_FLAG + 1;
        const int MESSAGE_WRITE_POS = PURGE_COMPLETE_FLAG + 1;

        ToggleWriter? setReadyForPurge;
        ToggleWriter? setPurgeComplete;

        ToggleReader? isReadyForPurge;
        ToggleReader? isPurgeComplete;

        public int PurgeSizeInBytes { get; }
        public bool IsolatedIo { get; }

        public ReusableFile(
                    string readFromFilename,
                    string writeToFilename,
                    int purgeSizeInBytes,
                    int tunnelTimeoutMilliseconds,
                    bool isolatedIo,
                    bool verbose) : base(readFromFilename, writeToFilename, tunnelTimeoutMilliseconds, verbose)
        {
            PurgeSizeInBytes = purgeSizeInBytes;
            IsolatedIo = isolatedIo;

            //FPS 11/11/2025: This class can write multiple commands to the file.
            //But there doesn't seem to be a performance improvement, so leaving as 1 for now.
            SendQueue = new BlockingCollection<Command>(1);

            if (Options.WriteIntervalMilliseconds > 0)
            {
                WriteLimiter = new FixedWindowRateLimiter(
                    new FixedWindowRateLimiterOptions()
                    {
                        PermitLimit = 1,
                        Window = TimeSpan.FromMilliseconds(Options.WriteIntervalMilliseconds),
                        QueueLimit = int.MaxValue,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst
                    });
            }

            if (Options.ReadIntervalMilliseconds > 0)
            {
                ReadLimiter = new FixedWindowRateLimiter(
                    new FixedWindowRateLimiterOptions()
                    {
                        PermitLimit = 1,
                        Window = TimeSpan.FromMilliseconds(Options.ReadIntervalMilliseconds),
                        QueueLimit = int.MaxValue,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst
                    });
            }
        }

        private readonly ReplenishingRateLimiter? WriteLimiter;
        private readonly ReplenishingRateLimiter? ReadLimiter;

        public override void SendPump()
        {
            //var debugFilename = $"diag-sent-{Environment.MachineName}.txt";
            //File.Create(debugFilename).Close();

            var writeFileShortName = Path.GetFileName(WriteToFilename);

            while (true)
            {
                Stream fileStream;

                try
                {
                    if (IsolatedIo)
                    {
                        //No held handle: each write opens/writes/closes (IsolatedWritesFileStream), so a
                        //counterpart can read the file between our writes - required for a server whose files
                        //open exclusively. The SetLength(MESSAGE_WRITE_POS) below truncates it to a fresh session.
                        fileStream = new IsolatedWritesFileStream(WriteToFilename, TunnelTimeoutMilliseconds);
                    }
                    else
                    {
                        //buffer must comfortably hold a full purge cycle, capped at 1 GB
                        var bufferSize = (int)Math.Clamp(PurgeSizeInBytes * 2L, 1024 * 1024, 1024 * 1024 * 1024);

                        //the writer always creates the file
                        fileStream = new FileStream(WriteToFilename, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite, bufferSize, Extensions.TunnelWriteOptions); //large buffer to prevent FileStream from autoflushing
                    }
                }
                catch (Exception ex)
                {
                    Program.Log($"Could not create file ({WriteToFilename}): {ex.Message}");
                    Delay.Wait(1000);
                    continue;
                }

                try
                {
                    fileStream.SetLength(MESSAGE_WRITE_POS);

                    var hashingStream = new HashingStream(fileStream, Verbose, TunnelTimeoutMilliseconds);
                    var binaryWriter = new BinaryWriter(hashingStream);

                    //Never 0: the header reads as 0 until this id is written, which tells the reader to wait for it.
                    var sessionId = Random.Shared.NextInt64(1, long.MaxValue);
                    binaryWriter.Write(sessionId);
                    binaryWriter.Flush(Verbose, TunnelTimeoutMilliseconds);

                    if (Verbose)
                    {
                        Program.Log($"[{writeFileShortName}] Set Session ID to: {sessionId}");
                    }

                    Stream setReadyForPurgeStream = IsolatedIo
                        ? new IsolatedWritesFileStream(WriteToFilename, TunnelTimeoutMilliseconds)
                        : new FileStream(WriteToFilename, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, 1, FileOptions.SequentialScan | Extensions.TunnelWriteOptions);
                    setReadyForPurge = new ToggleWriter(
                        new BinaryWriter(setReadyForPurgeStream),
                        READY_FOR_PURGE_FLAG,
                        TunnelTimeoutMilliseconds,
                        Verbose);

                    Stream setPurgeCompleteStream = IsolatedIo
                        ? new IsolatedWritesFileStream(WriteToFilename, TunnelTimeoutMilliseconds)
                        : new FileStream(WriteToFilename, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, 1, FileOptions.SequentialScan | Extensions.TunnelWriteOptions);
                    setPurgeComplete = new ToggleWriter(
                        new BinaryWriter(setPurgeCompleteStream),
                        PURGE_COMPLETE_FLAG,
                        TunnelTimeoutMilliseconds,
                        Verbose);

                    var ms = new MemoryStream();
                    var hashingMemoryStream = new HashingStream(ms, Verbose, TunnelTimeoutMilliseconds);
                    var msWriter = new BinaryWriter(hashingMemoryStream);

                    fileStream.Seek(MESSAGE_WRITE_POS, SeekOrigin.Begin);

                    var bytesWritten = 0L;

                    while (true)
                    {
                        var commandsSent = 0;
                        int? commandsToSend = null;

                        while (true)
                        {
                            hashingStream.Reset();

                            if (SendQueue.TryTake(out Command? command, TunnelTimeoutMilliseconds))
                            {
                                AssignSendSequence(command);

                                commandsToSend ??= SendQueue.Count + 1;

                                if (Verbose)
                                {
                                    Program.Log($"[{writeFileShortName}] Preparing to send packet number {command.PacketNumber} ({command.GetName()})");
                                }

                                hashingMemoryStream.SetLength(0);
                                command.Serialise(msWriter);
                                msWriter.Flush(Verbose, TunnelTimeoutMilliseconds);

                                if (PurgeSizeInBytes > 0 && fileStream.Position + hashingMemoryStream.Length >= PurgeSizeInBytes - MESSAGE_WRITE_POS)
                                {
                                    Program.Log($"[{writeFileShortName}] Instructing counterpart to prepare for purge.");

                                    var purge = new Purge();
                                    purge.Serialise(binaryWriter);

                                    binaryWriter.Flush(Verbose, TunnelTimeoutMilliseconds);

                                    if (Verbose)
                                    {
                                        Program.Log($"[{writeFileShortName}] Waiting for counterpart to be ready for purge.");
                                    }
                                    isReadyForPurge?.Wait(1);

                                    if (Verbose)
                                    {
                                        Program.Log($"[{writeFileShortName}] Performing truncation.");
                                    }
                                    fileStream.Seek(MESSAGE_WRITE_POS, SeekOrigin.Begin);
                                    fileStream.SetLength(MESSAGE_WRITE_POS);
                                    //fileStream.Flush(true);

                                    if (Verbose)
                                    {
                                        Program.Log($"[{writeFileShortName}] Signaling that the purge is complete.");
                                    }
                                    setPurgeComplete.Set(1);

                                    if (Verbose)
                                    {
                                        Program.Log($"[{writeFileShortName}] Waiting for counterpart clear their ready flag.");
                                    }
                                    isReadyForPurge?.Wait(0);

                                    if (Verbose)
                                    {
                                        Program.Log($"[{writeFileShortName}] Clearing our complete flag.");
                                    }
                                    setPurgeComplete.Set(0);

                                    Program.Log($"[{writeFileShortName}] Purge complete.");
                                }

                                //write the message to file
                                var commandStartPos = fileStream.Position;

                                //Write straight out of the MemoryStream's own buffer. ToArray() would
                                //copy every command an extra time on the hot path; BinaryWriter takes a
                                //span, and ms is a parameterless MemoryStream so GetBuffer() is allowed.
                                binaryWriter.Write(ms.GetBuffer().AsSpan(0, (int)ms.Length));

                                var commandEndPos = fileStream.Position;

                                bytesWritten += hashingMemoryStream.Length;

                                if (Verbose)
                                {
                                    Program.Log($"[{writeFileShortName}] Wrote packet number {command.PacketNumber} ({command.GetName()}) to position {commandStartPos:N0} - {commandEndPos:N0} ({(commandEndPos - commandStartPos).BytesToString()})");
                                }

                                CommandSent(command);
                                commandsSent++;
                            }

                            if (commandsToSend.HasValue && commandsSent >= commandsToSend.Value)
                            {
                                break;
                            }
                        }

                        WriteLimiter.Wait();
                        binaryWriter.Flush(Verbose, TunnelTimeoutMilliseconds);


                        //File.AppendAllLines(debugFilename, [$"{ms.Length:N0} bytes, packet number {command.PacketNumber}", Convert.ToBase64String(ms.ToArray())]);

                        if (Verbose)
                        {
                            Program.Log($"[{writeFileShortName}] Serialised {commandsSent:N0} commands into {Path.GetFileName(WriteToFilename)} ({bytesWritten.BytesToString()})");
                        }

                        bytesWritten = 0;
                    }
                }
                catch (Exception ex)
                {
                    Program.Log($"[{writeFileShortName}] {nameof(SendPump)}: {ex.Message}");
                    Program.Log($"[{writeFileShortName}] Restarting {nameof(SendPump)}");
                    Delay.Wait(1000);
                }
            }
        }

        public static long ReadSessionId(BinaryReader binaryReader)
        {
            var originalPos = binaryReader.BaseStream.Position;
            binaryReader.BaseStream.Seek(SESSION_ID, SeekOrigin.Begin);
            var result = binaryReader.ReadInt64();

            binaryReader.BaseStream.Seek(originalPos, SeekOrigin.Begin);

            return result;
        }

        //The writer sizes a new file to the header before it writes its session id there, so for a moment the
        //file exists with an id of 0. A reader that took that for the id saw the real one a second later as
        //a new session, and dropped everything the counterpart had set up through it.
        static bool HasSessionId(string filename)
        {
            try
            {
                using var file = new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                Span<byte> sessionId = stackalloc byte[sizeof(long)];
                return file.ReadAtLeast(sessionId, sessionId.Length, throwOnEndOfStream: false) == sessionId.Length
                       && BitConverter.ToInt64(sessionId) != 0;
            }
            catch
            {
                return false;
            }
        }

        public override void ReceivePump()
        {
            //var debugFilename = $"diag-received-{Environment.MachineName}.txt";
            //File.Create(debugFilename).Close();

            var readFileShortName = Path.GetFileName(ReadFromFilename);
            var checkForSessionChange = new Stopwatch();

            long currentSessionId = -1;
            long? retryPos = null;

            // macOS smbfs serves a held read handle a stale view across a counterpart's purge (the
            // truncate-and-regrow that caps the file). The in-place MacDirectRefresh keeps the handle
            // coherent in steady state, but under load it intermittently can't reveal the writer's
            // post-purge appends before the offline timeout - the reader stalls, the tunnel is declared
            // offline and every carried connection is torn down mid-transfer. Reopening the handle at the
            // purge boundary re-syncs the smbfs client cache reliably (exactly what --isolated-io does
            // on every read, which is why IR never hits this). Measured: LMM 6/12 -> 12/12, 0 timeouts.
            //
            // The Linux 9p client has the same weakness, far more reliably: after the counterpart purges the
            // file, a held read handle does not see the data written afterwards, so a purge ends the tunnel
            // (the reader stalls, the tunnel goes offline after the timeout and the forward listener closes).
            // That cut transfers above --max-size (10 MB) short on QEMU virtio-9p from v3.1.0 until this was
            // added. Measured there (guest kernel 6.12): a 16 MB transfer died at the first purge in 7 of 9
            // runs and at a later purge in the other two; with the reopen, 16/16/64/64 MB and 8/16/32 MB with
            // a purge every 2 MB all pass. diod shows the same failure when forced into Normal. A WSL drive
            // copes without the reopen, and is unharmed by it (64 MB and a purge every 2 MB pass there too).
            //
            // Elsewhere a purged file reads coherently through the held handle, and an extra open is pure cost.
            var reopenReadHandleAfterPurge = RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                                             || Extensions.IsNinePFileSystem(ReadFromFilename);

            // macOS NFS: a held read handle's attribute cache extends toward acregmax (~10s) while it keeps
            // seeing a stale EOF, so a peer's appends don't become visible before the tunnel timeout - a
            // FRESH open revalidates in ~2-4s (measured on .33; nothing on the held fd - fstat, F_NOCACHE -
            // beats it). When stalled this long, reopen the read handle in place (see the wait loop below).
            // Gated to macOS NFS in NORMAL mode ONLY: a reopened smbfs handle is served the SAME stale bytes
            // (smbfs is fixed in place by MacDirectRefresh's F_NOCACHE read, not by a reopen), and IR already
            // reopens per read - so reopening in either case only interferes (both regressed when this was
            // macOS-wide).
            var reopenReadHandleWhenStalled = Extensions.IsMacNfsMount(ReadFromFilename) && !IsolatedIo;
            const int stallReopenMillis = 2000;

            while (true)
            {
                try
                {
                    Stream? fileStream = null;
                    BinaryReader? binaryReader = null;

                    try
                    {
                        while (true)
                        {
                            if (File.Exists(ReadFromFilename) && HasSessionId(ReadFromFilename))
                            {
                                Program.Log($"[{readFileShortName}] now exists. Reading.");
                                break;
                            }
                            Delay.Wait(200);
                        }

                        fileStream = IsolatedIo ?
                                        new IsolatedReadsFileStream(ReadFromFilename, TunnelTimeoutMilliseconds) :
                                        new FileStream(ReadFromFilename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        fileStream.DisableReadAheadIfMacNfs();

                        if (retryPos != null)
                        {
                            Program.Log($"[{readFileShortName}] opened. Retrying read from position ({retryPos.Value:N0})");
                            fileStream.Seek(retryPos.Value, SeekOrigin.Begin);
                            retryPos = null;
                        }
                        else if (currentSessionId == -1)
                        {
                            Program.Log($"[{readFileShortName}] already existed. Seeking to end ({fileStream.Length:N0})");
                            fileStream.Seek(0, SeekOrigin.End);
                        }
                        else
                        {
                            Program.Log($"[{readFileShortName}] opened. Seeking to ({MESSAGE_WRITE_POS:N0})");
                            fileStream.Seek(MESSAGE_WRITE_POS, SeekOrigin.Begin);
                        }

                        var hashingStream = new HashingStream(fileStream, Verbose, TunnelTimeoutMilliseconds);
                        binaryReader = new BinaryReader(hashingStream, Encoding.ASCII);

                        currentSessionId = ReadSessionId(binaryReader);
                        if (Verbose)
                        {
                            Program.Log($"[{readFileShortName}] Read Session ID: {currentSessionId}");
                        }
                        SessionChanged?.Invoke(this, new());


                        // On macOS NFS the purge-handshake flag bytes go stale on a held read handle exactly
                        // like the data does, stalling the handshake into an offline teardown (the reader can't
                        // see the counterpart flip a flag). These flag reads are tiny and infrequent (only
                        // during a purge), so read them the always-fresh IR way - IsolatedReadsFileStream
                        // reopens + F_NOCACHE-reads on every read. The bulk data read stays the fast reopen-
                        // when-stalled FileStream; only these flags pay the per-read cost. NFS-gated: smbfs
                        // Normal reads the flags fine in place via MacDirectRefresh, so leave it untouched.
                        var freshFlagReads = IsolatedIo || Extensions.IsMacNfsMount(ReadFromFilename);

                        Stream isReadyForPurgeStream = freshFlagReads ?
                                                            new IsolatedReadsFileStream(ReadFromFilename, TunnelTimeoutMilliseconds) :
                                                            new FileStream(ReadFromFilename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.SequentialScan);
                        isReadyForPurge = new ToggleReader(
                            new BinaryReader(isReadyForPurgeStream, Encoding.ASCII),
                            READY_FOR_PURGE_FLAG,
                            TunnelTimeoutMilliseconds,
                            Verbose);

                        Stream isPurgeCompleteStream = freshFlagReads ?
                                                            new IsolatedReadsFileStream(ReadFromFilename, TunnelTimeoutMilliseconds) :
                                                            new FileStream(ReadFromFilename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.SequentialScan);
                        isPurgeComplete = new ToggleReader(
                            new BinaryReader(isPurgeCompleteStream, Encoding.ASCII),
                            PURGE_COMPLETE_FLAG,
                            TunnelTimeoutMilliseconds,
                            Verbose);
                    }
                    catch (Exception ex)
                    {
                        var exMsg = $"[{readFileShortName}] Establish file: {ex}";
                        if (Verbose)
                        {
                            Program.Log(exMsg);
                        }
                        throw new Exception(exMsg);
                    }

                    checkForSessionChange.Restart();
                    while (true)
                    {
                        var waitForData = Stopwatch.StartNew();
                        var sinceReopen = Stopwatch.StartNew();
                        while (true)
                        {
                            var nextByte = binaryReader.PeekChar();
                            if (nextByte != -1 && nextByte != 0)
                            {
                                break;
                            }

                            fileStream.ForceRead(TunnelTimeoutMilliseconds, Verbose);

                            // macOS NFS: reopen the read handle in place to defeat the held handle's stale
                            // attribute cache (see stallReopenMillis). We resume from the SAME byte position,
                            // so no command is skipped or re-read; CRC-safe because the per-command hash resets
                            // on each Command.Deserialise, so the fresh HashingStream carries no state. Each
                            // reopen's GETATTR reveals ALL data written so far, so the reader bulk-reads the
                            // backlog and stalls again - unlike IR, which reopens on every read. Only on a real
                            // stall, so smbfs (kept flowing by MacDirectRefresh) never triggers it.
                            if (reopenReadHandleWhenStalled
                                && waitForData.ElapsedMilliseconds > stallReopenMillis
                                && sinceReopen.ElapsedMilliseconds > stallReopenMillis)
                            {
                                var resumePos = fileStream.Position;
                                binaryReader.Dispose();
                                fileStream = IsolatedIo
                                    ? new IsolatedReadsFileStream(ReadFromFilename, TunnelTimeoutMilliseconds)
                                    : new FileStream(ReadFromFilename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                                fileStream.Seek(resumePos, SeekOrigin.Begin);
                                fileStream.DisableReadAheadIfMacNfs();
                                binaryReader = new BinaryReader(new HashingStream(fileStream, Verbose, TunnelTimeoutMilliseconds), Encoding.ASCII);
                                sinceReopen.Restart();
                            }

                            if (checkForSessionChange.ElapsedMilliseconds > 1000)
                            {
                                if (Verbose)
                                {
                                    Program.Log($"[{readFileShortName}] waiting for data at position {fileStream.Position:N0}.");
                                }

                                var latestSessionId = ReadSessionId(binaryReader);

                                if (latestSessionId != currentSessionId)
                                {
                                    var exMsg = $"New session detected: {latestSessionId}";
                                    if (Verbose)
                                    {
                                        Program.Log(exMsg);
                                    }
                                    throw new Exception(exMsg);
                                }

                                checkForSessionChange.Restart();
                            }

                            if (waitForData.ElapsedMilliseconds > TunnelTimeoutMilliseconds)
                            {
                                currentSessionId = -1;
                                throw new Exception($"[{readFileShortName}] Timed out while waiting for data.");
                            }

                            Delay.Wait(1);
                        }

                        ReadLimiter.Wait();

                        var commandStartPos = fileStream.Position;
                        Command? command;

                        //A partially-visible command is normal on a transport that doesn't publish a
                        //writer's append atomically - the VirtualBox shared folder and RDP's \\tsclient
                        //redirector both hand us a Forward whose payload is still arriving ("Forward
                        //payload truncated: read N of 65535 bytes"). The bytes are in flight, not corrupt,
                        //so re-read the same command in place: seek back, refresh the view, try again.
                        //
                        //This used to fall straight through to the restart path below, which reopens the
                        //file, re-reads the session id and costs ~1.5s. A couple of those in a row aged the
                        //keepalive past the tunnel timeout, so the tunnel went offline and tore down every
                        //carried connection mid-transfer - surfacing as a truncated payload in the test.
                        //Recovering in place keeps it to milliseconds and leaves the ping unstarved.
                        //
                        //Bounded, and the old restart path is still the fallback: if the data really is
                        //corrupt rather than late, this gives up after ReadRetryMilliseconds and behaves
                        //exactly as before.
                        var readAttempt = Stopwatch.StartNew();
                        while (true)
                        {
                            try
                            {
                                command = Command.Deserialise(binaryReader);
                                break;
                            }
                            catch when (readAttempt.ElapsedMilliseconds < ReadRetryMilliseconds)
                            {
                                fileStream.Seek(commandStartPos, SeekOrigin.Begin);
                                fileStream.ForceRead(TunnelTimeoutMilliseconds, Verbose);
                                Delay.Wait(1);
                            }
                            catch
                            {
                                retryPos = commandStartPos;
                                Delay.Wait(500);
                                throw;
                            }
                        }

                        if (Verbose && readAttempt.ElapsedMilliseconds > 0)
                        {
                            Program.Log($"[{readFileShortName}] Re-read a partially-visible command at position {commandStartPos:N0} after {readAttempt.ElapsedMilliseconds:N0} ms.");
                        }

                        var commandEndPos = fileStream.Position;

                        //var ms = new MemoryStream();
                        //fileStream.Seek(commandStartPos, SeekOrigin.Begin);
                        //fileStream.CopyTo(ms, commandEndPos - commandStartPos);
                        //File.AppendAllLines(debugFilename, [$"{ms.Length:N0} bytes, packet number {command.PacketNumber}", Convert.ToBase64String(ms.ToArray())]);

                        //if (binaryReader.BaseStream is HashingStream hs)
                        //{
                        //    var hashingStreamBytes = hs.GetData();
                        //    var ms = new MemoryStream(hashingStreamBytes);
                        //    File.AppendAllLines(debugFilename, [$"{ms.Length:N0} bytes, packet number {command.PacketNumber}", Convert.ToBase64String(ms.ToArray())]);
                        //}



                        if (command == null)
                        {
                            var exMsg = $"[{readFileShortName}] Could not read command at file position {commandStartPos:N0}. [{ReadFromFilename}]";
                            if (Verbose)
                            {
                                Program.Log(exMsg);
                            }
                            throw new Exception(exMsg);
                        }

                        if (Verbose)
                        {
                            Program.Log($"[{readFileShortName}] Received packet number {command.PacketNumber} ({command.GetName()}) from position {commandStartPos:N0} - {commandEndPos:N0} ({(commandEndPos - commandStartPos).BytesToString()})");
                        }

                        CommandReceived(command);

                        if (command is Purge)
                        {
                            Program.Log($"[{readFileShortName}] Counterpart is about to purge this file.");

                            if (Verbose)
                            {
                                Program.Log($"[{readFileShortName}] Signaling that we're ready for purge.");
                            }
                            setReadyForPurge?.Set(1);

                            if (Verbose)
                            {
                                Program.Log($"[{readFileShortName}] Waiting for the purge to be complete.");
                            }
                            isPurgeComplete.Wait(1);

                            if (Verbose)
                            {
                                Program.Log($"[{readFileShortName}] Seeking to the beginning of file.");
                            }
                            fileStream.Seek(MESSAGE_WRITE_POS, SeekOrigin.Begin);

                            if (Verbose)
                            {
                                Program.Log($"[{readFileShortName}] Clearing ready flag.");
                            }
                            setReadyForPurge?.Set(0);

                            if (Verbose)
                            {
                                Program.Log($"[{readFileShortName}] Waiting for counterpart to clear the complete flag.");
                            }
                            isPurgeComplete.Wait(0);

                            Program.Log($"[{readFileShortName}] File was purged by counterpart.");

                            // Reopen the read handle here (macOS only - see reopenReadHandleAfterPurge) to
                            // re-sync the smbfs client cache across the purge. CRC-safe: the per-command hash
                            // resets on each Command.Deserialise, so a fresh HashingStream carries no state.
                            if (reopenReadHandleAfterPurge)
                            {
                                binaryReader.Dispose();   // disposes the wrapping HashingStream + fileStream
                                fileStream = IsolatedIo
                                    ? new IsolatedReadsFileStream(ReadFromFilename, TunnelTimeoutMilliseconds)
                                    : new FileStream(ReadFromFilename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                                fileStream.Seek(MESSAGE_WRITE_POS, SeekOrigin.Begin);
                                binaryReader = new BinaryReader(new HashingStream(fileStream, Verbose, TunnelTimeoutMilliseconds), Encoding.ASCII);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Program.Log($"[{readFileShortName}] {nameof(ReceivePump)}: {ex.Message}");
                    Program.Log($"[{readFileShortName}] Restarting {nameof(ReceivePump)}");
                    Delay.Wait(1000);
                }
            }
        }


        public override void Stop(string reason)
        {
            Program.Log($"{nameof(ReusableFile)}: Stopping. Reason: {reason}");
        }
    }
}
