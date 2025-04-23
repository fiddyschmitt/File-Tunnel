using ft.Bandwidth;
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
using System.Threading.Tasks;

namespace ft.Listeners
{
    public class SharedFileManager : ASharedFileManager
    {
        const long SESSION_ID = 0;
        const int READY_FOR_PURGE_FLAG = sizeof(long);
        const int PURGE_COMPLETE_FLAG = READY_FOR_PURGE_FLAG + 1;
        const int MESSAGE_WRITE_POS = PURGE_COMPLETE_FLAG + 1;
        private readonly int readDurationMilliseconds;
        ToggleWriter? setReadyForPurge;
        ToggleWriter? setPurgeComplete;

        public int PurgeSizeInBytes { get; }
        public bool IsolatedReads { get; }

        public SharedFileManager(
                    string readFromFilename,
                    string writeToFilename,
                    int purgeSizeInBytes,
                    int readDurationMilliseconds,
                    int tunnelTimeoutMilliseconds,
                    bool isolatedReads,
                    bool verbose) : base(readFromFilename, writeToFilename, tunnelTimeoutMilliseconds, verbose)
        {
            PurgeSizeInBytes = purgeSizeInBytes;
            this.readDurationMilliseconds = readDurationMilliseconds;
            IsolatedReads = isolatedReads;
        }

        public override void SendPump()
        {
            var writeFileShortName = Path.GetFileName(WriteToFilename);
            var writingStopwatch = new Stopwatch();

            while (true)
            {
                FileStream fileStream;

                try
                {
                    var bufferSize = PurgeSizeInBytes * 2;
                    bufferSize = Math.Max(bufferSize, 1024 * 1024 * 1024);

                    //the writer always creates the file
                    fileStream = new FileStream(WriteToFilename, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite, bufferSize); //large buffer to prevent FileStream from autoflushing
                }
                catch (Exception ex)
                {
                    Program.Log($"Could not create file ({WriteToFilename}): {ex.Message}");
                    Thread.Sleep(1000);
                    continue;
                }

                try
                {
                    fileStream.SetLength(MESSAGE_WRITE_POS);

                    var hashingStream = new HashingStream(fileStream);
                    var binaryWriter = new BinaryWriter(hashingStream);

                    var sessionId = Program.Random.NextInt64();
                    binaryWriter.Write(sessionId);
                    binaryWriter.Flush();

                    if (Verbose)
                    {
                        Program.Log($"[{writeFileShortName}] Set Session ID to: {sessionId}");
                    }

                    var setReadyForPurgeStream = new FileStream(WriteToFilename, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, 1, FileOptions.SequentialScan);
                    setReadyForPurge = new ToggleWriter(
                        new BinaryWriter(setReadyForPurgeStream),
                        READY_FOR_PURGE_FLAG);

                    var setPurgeCompleteStream = new FileStream(WriteToFilename, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, 1, FileOptions.SequentialScan);
                    setPurgeComplete = new ToggleWriter(
                        new BinaryWriter(setPurgeCompleteStream),
                        PURGE_COMPLETE_FLAG);

                    var ms = new HashingStream(new MemoryStream());
                    var msWriter = new BinaryWriter(ms);

                    fileStream.Seek(MESSAGE_WRITE_POS, SeekOrigin.Begin);

                    var commandsWritten = 0;
                    //write as many commands as possible to the file
                    while (true)
                    {
                        var command = SendQueue.Take();

                        if (commandsWritten == 0)
                        {
                            writingStopwatch.Restart();
                        }

                        ms.SetLength(0);
                        command.Serialise(msWriter);
                        msWriter.Flush();

                        if (PurgeSizeInBytes > 0 && fileStream.Position + ms.Length >= PurgeSizeInBytes - MESSAGE_WRITE_POS)
                        {
                            Program.Log($"[{writeFileShortName}] Instructing counterpart to prepare for purge.");

                            var purge = new Purge();
                            purge.Serialise(binaryWriter);
                            fileStream.Flush();

                            //wait for counterpart to be ready for purge
                            isReadyForPurge?.Wait(1);

                            //perform the purge
                            fileStream.Seek(MESSAGE_WRITE_POS, SeekOrigin.Begin);
                            fileStream.SetLength(MESSAGE_WRITE_POS);

                            //signal that the purge is complete
                            setPurgeComplete.Set(1);

                            //wait for counterpart clear their ready flag
                            isReadyForPurge?.Wait(0);

                            //clear our complete flag
                            setPurgeComplete.Set(0);

                            Program.Log($"[{writeFileShortName}] Purge complete.");
                        }

                        //write the message to file
                        var commandStartPos = fileStream.Position;
                        command.Serialise(binaryWriter);
                        var commandEndPos = fileStream.Position;
                        commandsWritten++;

                        if (Verbose)
                        {
                            Program.Log($"[{writeFileShortName}] Wrote packet number {command.PacketNumber:N0} ({command.GetName()}) to position {commandStartPos:N0} - {commandEndPos:N0} ({(commandEndPos - commandStartPos).BytesToString()})");
                        }

                        CommandSent(command);

                        if (SendQueue.Count == 0 || writingStopwatch.ElapsedMilliseconds > readDurationMilliseconds)
                        {
                            binaryWriter.Flush();
                            commandsWritten = 0;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Program.Log($"[{writeFileShortName}] {nameof(SendPump)}: {ex.Message}");
                    Program.Log($"[{writeFileShortName}] Restarting {nameof(SendPump)}");
                    Thread.Sleep(1000);
                }
            }
        }

        ToggleReader? isReadyForPurge;
        ToggleReader? isPurgeComplete;

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
                            Thread.Sleep(200);
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

                        var hashingStream = new HashingStream(fileStream);
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
                            READY_FOR_PURGE_FLAG);

                        Stream isPurgeCompleteStream = IsolatedReads ?
                                                            new IsolatedReadsFileStream(ReadFromFilename) :
                                                            new FileStream(ReadFromFilename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.SequentialScan);
                        isPurgeComplete = new ToggleReader(
                            new BinaryReader(isPurgeCompleteStream, Encoding.ASCII),
                            PURGE_COMPLETE_FLAG);
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
                        while (true)
                        {
                            var nextByte = binaryReader.PeekChar();
                            if (nextByte != -1 && nextByte != 0)
                            {
                                break;
                            }

                            //force read
                            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                            {
                                using var tempFs = new FileStream(ReadFromFilename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                                tempFs.Read(new byte[4096]);
                            }
                            else
                            {
                                fileStream.Flush();
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

                            Delay.Wait(1);
                        }

                        var commandStartPos = fileStream.Position;
                        Command? command;

                        try
                        {
                            command = Command.Deserialise(binaryReader);
                        }
                        catch
                        {
                            retryPos = commandStartPos;
                            Thread.Sleep(500);
                            throw;
                        }

                        var commandEndPos = fileStream.Position;

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
                            Program.Log($"[{readFileShortName}] Received packet number {command.PacketNumber:N0} ({command.GetName()}) from position {commandStartPos:N0} - {commandEndPos:N0} ({(commandEndPos - commandStartPos).BytesToString()})");
                        }

                        CommandReceived(command);

                        if (command is Purge)
                        {
                            Program.Log($"[{readFileShortName}] Counterpart is about to purge this file.");

                            //signal that we're ready for purge
                            setReadyForPurge?.Set(1);

                            //wait for the purge to be complete
                            isPurgeComplete.Wait(1);

                            //go back to the beginning
                            fileStream.Seek(MESSAGE_WRITE_POS, SeekOrigin.Begin);
                            fileStream.Flush(); //force read

                            //clear our ready flag
                            setReadyForPurge?.Set(0);

                            //wait for counterpart to clear the complete flag
                            isPurgeComplete.Wait(0);

                            Program.Log($"[{readFileShortName}] File was purged by counterpart.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Program.Log($"[{readFileShortName}] {nameof(ReceivePump)}: {ex.Message}");
                    Program.Log($"[{readFileShortName}] Restarting {nameof(ReceivePump)}");
                    Thread.Sleep(1000);
                }
            }
        }


        public override void Stop(string reason)
        {
            Program.Log($"{nameof(SharedFileManager)}: Stopping. Reason: {reason}");
        }
    }
}
