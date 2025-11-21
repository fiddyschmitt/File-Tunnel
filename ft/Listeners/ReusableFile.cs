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
        const long SESSION_ID = 0;
        const int READY_FOR_PURGE_FLAG = sizeof(long);
        const int PURGE_COMPLETE_FLAG = READY_FOR_PURGE_FLAG + 1;
        const int MESSAGE_WRITE_POS = PURGE_COMPLETE_FLAG + 1;

        ToggleWriter? setReadyForPurge;
        ToggleWriter? setPurgeComplete;

        ToggleReader? isReadyForPurge;
        ToggleReader? isPurgeComplete;

        public int PurgeSizeInBytes { get; }
        public bool IsolatedReads { get; }

        public ReusableFile(
                    string readFromFilename,
                    string writeToFilename,
                    int purgeSizeInBytes,
                    int tunnelTimeoutMilliseconds,
                    bool isolatedReads,
                    bool verbose) : base(readFromFilename, writeToFilename, tunnelTimeoutMilliseconds, verbose)
        {
            PurgeSizeInBytes = purgeSizeInBytes;
            IsolatedReads = isolatedReads;

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
                FileStream fileStream;

                try
                {
                    var bufferSize = PurgeSizeInBytes * 2;
                    bufferSize = Math.Max(bufferSize, 1024 * 1024 * 1024);

                    //the writer always creates the file
                    fileStream = new FileStream(WriteToFilename, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite, bufferSize, FileOptions.WriteThrough); //large buffer to prevent FileStream from autoflushing
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

                    var sessionId = Random.Shared.NextInt64();
                    binaryWriter.Write(sessionId);
                    binaryWriter.Flush(Verbose, TunnelTimeoutMilliseconds);

                    if (Verbose)
                    {
                        Program.Log($"[{writeFileShortName}] Set Session ID to: {sessionId}");
                    }

                    var setReadyForPurgeStream = new FileStream(WriteToFilename, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, 1, FileOptions.SequentialScan | FileOptions.WriteThrough);
                    setReadyForPurge = new ToggleWriter(
                        new BinaryWriter(setReadyForPurgeStream),
                        READY_FOR_PURGE_FLAG,
                        TunnelTimeoutMilliseconds,
                        Verbose);

                    var setPurgeCompleteStream = new FileStream(WriteToFilename, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, 1, FileOptions.SequentialScan | FileOptions.WriteThrough);
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

                                var commandBytes = ms.ToArray();
                                binaryWriter.Write(commandBytes);

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

        public override void ReceivePump()
        {
            //var debugFilename = $"diag-received-{Environment.MachineName}.txt";
            //File.Create(debugFilename).Close();

            var readFileShortName = Path.GetFileName(ReadFromFilename);
            var checkForSessionChange = new Stopwatch();

            long currentSessionId = -1;
            long? retryPos = null;

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
                            if (File.Exists(ReadFromFilename) && new FileInfo(ReadFromFilename).Length > 0)
                            {
                                Program.Log($"[{readFileShortName}] now exists. Reading.");
                                break;
                            }
                            Delay.Wait(200);
                        }

                        fileStream = IsolatedReads ?
                                        new IsolatedReadsFileStream(ReadFromFilename) :
                                        new FileStream(ReadFromFilename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

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


                        Stream isReadyForPurgeStream = IsolatedReads ?
                                                            new IsolatedReadsFileStream(ReadFromFilename) :
                                                            new FileStream(ReadFromFilename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.SequentialScan);
                        isReadyForPurge = new ToggleReader(
                            new BinaryReader(isReadyForPurgeStream, Encoding.ASCII),
                            READY_FOR_PURGE_FLAG,
                            TunnelTimeoutMilliseconds,
                            Verbose);

                        Stream isPurgeCompleteStream = IsolatedReads ?
                                                            new IsolatedReadsFileStream(ReadFromFilename) :
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
                        while (true)
                        {
                            var nextByte = binaryReader.PeekChar();
                            if (nextByte != -1 && nextByte != 0)
                            {
                                break;
                            }

                            fileStream.ForceRead(TunnelTimeoutMilliseconds, Verbose);

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

                        try
                        {
                            command = Command.Deserialise(binaryReader);
                        }
                        catch
                        {
                            retryPos = commandStartPos;
                            Delay.Wait(500);
                            throw;
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
