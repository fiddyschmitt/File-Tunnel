﻿using ft.Bandwidth;
using ft.Commands;
using ft.IO;
using ft.Listeners;
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

namespace ft.Streams
{
    public class SharedFileManager(
        string readFromFilename,
        string writeToFilename,
        int purgeSizeInBytes,
        int tunnelTimeoutMilliseconds,
        bool isolatedReads,
        bool verbose) : StreamEstablisher
    {
        readonly ConcurrentDictionary<int, BlockingCollection<byte[]>> ReceiveQueue = [];
        readonly BlockingCollection<Command> SendQueue = new(1);    //using a queue size of one makes the TCP receiver synchronous

        public event EventHandler<CreateLocalListenerEventArgs>? CreateLocalListenerRequested;
        public event EventHandler? SessionChanged;

        const int reportIntervalMs = 1000;
        readonly BandwidthTracker sentBandwidth = new(100, reportIntervalMs);
        readonly BandwidthTracker receivedBandwidth = new(100, reportIntervalMs);
        readonly BlockingCollection<Ping> pingResponsesReceived = [];
        public void ReportNetworkPerformance()
        {
            while (true)
            {
                try
                {
                    var sentBandwidthStr = sentBandwidth.GetBandwidth();
                    var receivedBandwidthStr = receivedBandwidth.GetBandwidth();

                    lock (Program.ConsoleOutputLock)
                    {
                        Console.Write($"{DateTime.Now}: Counterpart: ");

                        if (IsOnline)
                        {
                            Console.ForegroundColor = ConsoleColor.Green;
                            Console.Write($"{"Online",-10}");

                            var logStr = $"Rx: {receivedBandwidthStr,-12} Tx: {sentBandwidthStr,-12}";


                            if (latestRTT != null)
                            {
                                logStr += $" {latestRTT.Value.Milliseconds:N0} ms";
                                latestRTT = null;
                            }

                            Console.ForegroundColor = Program.OriginalConsoleColour;
                            Console.WriteLine(logStr);
                        }
                        else
                        {
                            Console.ForegroundColor = ConsoleColor.Red;
                            Console.Write($"{"Offline",-10}");

                            var offlineReason = "Counterpart is not responding.";

                            if (!receiveFileEstablished)
                            {
                                offlineReason = $"Receive file ({Path.GetFileName(ReadFromFilename)}) is not established.";
                            }
                            else if (!sendFileEstablished)
                            {
                                offlineReason = $"Send file ({Path.GetFileName(WriteToFilename)}) is not established.";
                            }

                            Console.ForegroundColor = Program.OriginalConsoleColour;
                            Console.WriteLine(offlineReason);
                        }
                    }

                    Thread.Sleep(reportIntervalMs);
                }
                catch (Exception ex)
                {
                    Program.Log($"{ex}");
                }
            }
        }

        public byte[]? Read(int connectionId)
        {
            if (!ReceiveQueue.TryGetValue(connectionId, out var connectionReceiveQueue))
            {
                return null;
            }

            byte[]? result = null;
            try
            {
                result = connectionReceiveQueue.Take();
            }
            catch (InvalidOperationException)
            {
                //This is normal - the queue might have been marked as AddingComplete while we were listening
            }

            return result;
        }

        public int GenerateUniqueConnectionId()
        {
            int result;
            while (true)
            {
                result = Program.Random.Next(int.MaxValue);

                if (!ReceiveQueue.ContainsKey(result))
                {
                    break;
                }
            }

            return result;
        }

        public void Connect(int connectionId, string destinationEndpointStr)
        {
            var connectCommand = new Connect(connectionId, destinationEndpointStr);
            if (EnqueueToSend(connectCommand))
            {
                if (!ReceiveQueue.TryGetValue(connectionId, out _))
                {
                    ReceiveQueue.TryAdd(connectionId, []);
                }
            }
        }

        public void Write(int connectionId, byte[] data)
        {
            var forwardCommand = new Forward(connectionId, data);
            EnqueueToSend(forwardCommand);
        }

        public void TearDown(int connectionId)
        {
            var teardownCommand = new TearDown(connectionId);

            if (IsOnline)
            {
                EnqueueToSend(teardownCommand);
            }

            if (ReceiveQueue.TryGetValue(connectionId, out var receiveQueue))
            {
                receiveQueue.CompleteAdding();
                ReceiveQueue.TryRemove(connectionId, out _);
            }
        }

        public bool EnqueueToSend(Command cmd)
        {
            bool result;
            try
            {
                SendQueue.TryAdd(cmd, tunnelTimeoutMilliseconds);
                result = true;
            }
            catch
            {
                result = false;
            }

            return result;
        }

        const long SESSION_ID = 0;
        const int READY_FOR_PURGE_FLAG = sizeof(long);
        const int PURGE_COMPLETE_FLAG = READY_FOR_PURGE_FLAG + 1;
        const int MESSAGE_WRITE_POS = PURGE_COMPLETE_FLAG + 1;

        ToggleWriter? setReadyForPurge;
        ToggleWriter? setPurgeComplete;

        bool sendFileEstablished = false;

        public void SendPump()
        {
            var writeFileShortName = Path.GetFileName(WriteToFilename);

            while (true)
            {
                sendFileEstablished = false;
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

                    sendFileEstablished = true;

                    foreach (var command in SendQueue.GetConsumingEnumerable())
                    {
                        ms.SetLength(0);
                        command.Serialise(msWriter);

                        if (PurgeSizeInBytes > 0 && fileStream.Position + ms.Length >= PurgeSizeInBytes - MESSAGE_WRITE_POS)
                        {
                            Program.Log($"[{writeFileShortName}] Instructing counterpart to prepare for purge.");

                            var purge = new Purge();
                            purge.Serialise(binaryWriter);

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

                        if (Verbose)
                        {
                            Program.Log($"[{writeFileShortName}] Wrote packet number {command.PacketNumber:N0} ({command.GetName()}) to position {commandStartPos:N0} - {commandEndPos:N0} ({(commandEndPos - commandStartPos).BytesToString()})");
                        }

                        if (command is Forward forward && forward.Payload != null)
                        {
                            var totalBytesSent = sentBandwidth.TotalBytesTransferred + (ulong)forward.Payload.Length;
                            sentBandwidth.SetTotalBytesTransferred(totalBytesSent);
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

        bool receiveFileEstablished = false;

        public void ReceivePump()
        {
            var readFileShortName = Path.GetFileName(ReadFromFilename);
            var checkForSessionChange = new Stopwatch();

            long currentSessionId = -1;
            long? retryPos = null;

            while (true)
            {
                try
                {
                    receiveFileEstablished = false;

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

                    receiveFileEstablished = true;

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

                        lastContactWithCounterpart = DateTime.Now;

                        if (Verbose)
                        {
                            Program.Log($"[{readFileShortName}] Received packet number {command.PacketNumber:N0} ({command.GetName()}) from position {commandStartPos:N0} - {commandEndPos:N0} ({(commandEndPos - commandStartPos).BytesToString()})");
                        }

                        if (command is Forward forward && forward.Payload != null)
                        {
                            if (ReceiveQueue.TryGetValue(forward.ConnectionId, out var connectionReceiveQueue))
                            {
                                connectionReceiveQueue.Add(forward.Payload);

                                var totalBytesReceived = receivedBandwidth.TotalBytesTransferred + (ulong)(forward.Payload.Length);
                                receivedBandwidth.SetTotalBytesTransferred(totalBytesReceived);
                            }
                        }
                        else if (command is Connect connect)
                        {
                            if (!ReceiveQueue.ContainsKey(connect.ConnectionId))
                            {
                                ReceiveQueue.TryAdd(connect.ConnectionId, []);

                                Threads.StartNew(() =>
                                {
                                    var sharedFileStream = new SharedFileStream(this, connect.ConnectionId);
                                    ConnectionAccepted?.Invoke(this, new ConnectionAcceptedEventArgs(sharedFileStream, connect.DestinationEndpointString));
                                }, "ConnectionAccepted");
                            }
                        }
                        else if (command is CreateListener createListener)
                        {
                            var createLocalListenerEventArgs = new CreateLocalListenerEventArgs(createListener.Protocol, createListener.ForwardString);
                            CreateLocalListenerRequested?.Invoke(this, createLocalListenerEventArgs);
                        }
                        else if (command is Purge)
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
                        else if (command is TearDown teardown && ReceiveQueue.TryGetValue(teardown.ConnectionId, out var connectionReceiveQueue))
                        {
                            Program.Log($"[{readFileShortName}] Counterpart asked to tear down connection {teardown.ConnectionId}");

                            ReceiveQueue.Remove(teardown.ConnectionId, out _);

                            connectionReceiveQueue.CompleteAdding();
                        }
                        else if (command is Ping ping)
                        {
                            if (ping.PingType == EnumPingType.Request)
                            {
                                var response = new Ping(EnumPingType.Response)
                                {
                                    ResponseToPacketNumber = ping.PacketNumber
                                };

                                Task.Factory.StartNew(() =>
                                {
                                    //start in a new task, because we want to continue receiving messages while queuing this message may block
                                    EnqueueToSend(response);
                                });
                            }

                            if (ping.PingType == EnumPingType.Response)
                            {
                                pingResponsesReceived.Add(ping);
                            }
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

        public TimeSpan? latestRTT = null;
        public void MeasureRTT()
        {
            var pingRequest = new Ping(EnumPingType.Request);
            var pingStopwatch = new Stopwatch();

            while (true)
            {
                try
                {
                    pingStopwatch.Restart();

                    if (!EnqueueToSend(pingRequest))
                    {
                        latestRTT = null;
                        continue;
                    }

                    try
                    {
                        while (true)
                        {
                            var responseTimeout = new CancellationTokenSource(tunnelTimeoutMilliseconds);
                            var pingResponse = pingResponsesReceived.GetConsumingEnumerable(responseTimeout.Token).First();
                            if (pingRequest.PacketNumber == pingResponse.ResponseToPacketNumber)
                            {
                                pingStopwatch.Stop();

                                latestRTT = pingStopwatch.Elapsed;
                                break;
                            }
                        }
                    }
                    catch
                    {
                        latestRTT = null;
                        continue;
                    }

                    var durationToSleep = (int)(1000 - latestRTT?.TotalMilliseconds ?? 0);
                    if (durationToSleep < 0) durationToSleep = 0;
                    Thread.Sleep(durationToSleep);
                }
                catch (Exception ex)
                {
                    Program.Log($"{ex}");
                }
            }
        }

        public override void Start()
        {
            Threads.StartNew(ReceivePump, nameof(ReceivePump));
            Threads.StartNew(SendPump, nameof(SendPump));
            Threads.StartNew(MeasureRTT, nameof(MeasureRTT));
            Threads.StartNew(ReportNetworkPerformance, nameof(ReportNetworkPerformance));
            Threads.StartNew(MonitorOnlineStatus, nameof(MonitorOnlineStatus));

            if (Debugger.IsAttached)
            {
                //Threads.StartNew(WriteThreadReport, nameof(WriteThreadReport));
            }
        }

        public static void WriteThreadReport()
        {
            while (true)
            {
                var threads = Threads
                                    .CreatedThreads
                                    .Where(thread => thread.ThreadState != System.Threading.ThreadState.Stopped)
                                    .OrderBy(thread => thread.ThreadState)
                                    .ThenBy(thread => thread.Name)
                                    .Where(thread => !string.IsNullOrEmpty(thread.Name))
                                    .ToList();

                var threadStr = threads
                                    .Select((thread, index) => $"{index + 1:N0}/{threads.Count:N0} [{thread.ThreadState}] (Id {thread.ManagedThreadId}) {thread.Name}")
                                    .ToString(Environment.NewLine);

                Console.WriteLine(threadStr);

                Thread.Sleep(10000);
            }
        }

        DateTime? lastContactWithCounterpart = null;
        public bool IsOnline { get; protected set; } = false;
        public event EventHandler<OnlineStatusEventArgs>? OnlineStatusChanged;

        private void MonitorOnlineStatus()
        {
            try
            {
                while (true)
                {
                    if (lastContactWithCounterpart != null)
                    {
                        var orig = IsOnline;

                        var timeSinceLastContact = DateTime.Now - lastContactWithCounterpart.Value;
                        IsOnline = timeSinceLastContact.TotalMilliseconds < tunnelTimeoutMilliseconds;

                        if (orig != IsOnline)
                        {
                            OnlineStatusChanged?.Invoke(this, new OnlineStatusEventArgs(IsOnline));
                        }
                    }

                    Thread.Sleep(100);
                }
            }
            catch (Exception ex)
            {
                Program.Log($"{nameof(MonitorOnlineStatus)}: {ex.Message}");
            }
        }

        public override void Stop()
        {

        }

        public void TearDownAllConnections()
        {
            ReceiveQueue
                .Keys
                .ToList()
                .ForEach(connectionId =>
                {
                    TearDown(connectionId);
                });
        }

        public string WriteToFilename { get; } = writeToFilename;
        public int PurgeSizeInBytes { get; } = purgeSizeInBytes;
        public bool IsolatedReads { get; } = isolatedReads;
        public bool Verbose { get; } = verbose;
        public string ReadFromFilename { get; } = readFromFilename;
    }

    public class OnlineStatusEventArgs(bool isOnline)
    {
        public bool IsOnline { get; } = isOnline;
    }

    public class CreateLocalListenerEventArgs(string protocol, string forwardString)
    {
        public string Protocol { get; } = protocol;
        public string ForwardString { get; } = forwardString;
    }
}
