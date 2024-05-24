using ft.Bandwidth;
using ft.Commands;
using ft.IO;
using ft.Listeners;
using ft.Utilities;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net.Mail;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ft.Streams
{
    public class SharedFileManager : StreamEstablisher
    {
        readonly Dictionary<int, BlockingCollection<byte[]>> ReceiveQueue = [];
        readonly BlockingCollection<Command> SendQueue = new(1);    //using a queue size of one makes the TCP receiver synchronous

        public SharedFileManager(string readFromFilename, string writeToFilename)
        {
            ReadFromFilename = readFromFilename;
            WriteToFilename = writeToFilename;

            Task.Factory.StartNew(ReceivePump, TaskCreationOptions.LongRunning);
            Task.Factory.StartNew(SendPump, TaskCreationOptions.LongRunning);
            Task.Factory.StartNew(ReportNetworkPerformance, TaskCreationOptions.LongRunning);
        }

        const int reportIntervalMs = 1000;
        readonly BandwidthTracker sentBandwidth = new(100, reportIntervalMs);
        readonly BandwidthTracker receivedBandwidth = new(100, reportIntervalMs);
        readonly BlockingCollection<Ping> pingResponsesReceived = [];
        public void ReportNetworkPerformance()
        {
            var pingRequest = new Ping(EnumPingType.Request);
            var pingStopwatch = new Stopwatch();

            while (true)
            {
                try
                {
                    var sentBandwidthStr = sentBandwidth.GetBandwidth();
                    var receivedBandwidthStr = receivedBandwidth.GetBandwidth();

                    pingStopwatch.Restart();
                    SendQueue.Add(pingRequest);

                    string? pingDurationStr = null;

                    var responseTimeout = new CancellationTokenSource(5000);
                    try
                    {
                        while (true)
                        {
                            var pingResponse = pingResponsesReceived.GetConsumingEnumerable(responseTimeout.Token).First();
                            if (pingRequest.PacketNumber == pingResponse.ResponseToPacketNumber)
                            {
                                pingStopwatch.Stop();
                                pingDurationStr = $"RTT: {pingStopwatch.ElapsedMilliseconds:N0} ms";
                                break;
                            }
                        }
                    }
                    catch { }


                    var logStr = $"Read from file: {receivedBandwidthStr,-12} Wrote to file: {sentBandwidthStr,-12}";
                    if (pingDurationStr != null)
                    {
                        logStr += $" {pingDurationStr}";
                    }

                    Program.Log(logStr);

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
            if (!ReceiveQueue.TryGetValue(connectionId, out BlockingCollection<byte[]>? connectionReceiveQueue))
            {
                connectionReceiveQueue = new();
                ReceiveQueue.Add(connectionId, connectionReceiveQueue);
            }

            byte[]? result = null;
            try
            {
                result = connectionReceiveQueue.Take(cancellationTokenSource.Token);
            }
            catch (InvalidOperationException)
            {
                //This is normal - the queue might have been marked as AddingComplete while we were listening
            }

            return result;
        }

        public void Connect(int connectionId)
        {
            var connectCommand = new Connect(connectionId);
            SendQueue.Add(connectCommand);
        }

        public void Write(int connectionId, byte[] data)
        {
            var forwardCommand = new Forward(connectionId, data);
            SendQueue.Add(forwardCommand);
        }

        public void TearDown(int connectionId)
        {
            var teardownCommand = new TearDown(connectionId);
            SendQueue.Add(teardownCommand);

            ReceiveQueue.Remove(connectionId);
        }

        const int READY_FOR_PURGE_FLAG = 0;
        const int PURGE_COMPLETE_FLAG = 1;
        const int MESSAGE_WRITE_POS = 2;

        ToggleWriter? setReadyForPurge;
        ToggleWriter? setPurgeComplete;

        public void SendPump()
        {
            var writeFileShortName = Path.GetFileName(WriteToFilename);

            try
            {
                //the writer always creates the file
                var fileStream = new FileStream(WriteToFilename, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite, Program.SHARED_FILE_SIZE * 2); //large buffer to prevent FileStream from autoflushing
                fileStream.SetLength(MESSAGE_WRITE_POS);

                var binaryWriter = new BinaryWriter(fileStream);
                var binaryReader = new BinaryReader(fileStream, Encoding.ASCII);

                var setReadyForPurgeStream = new FileStream(WriteToFilename, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, 1, FileOptions.SequentialScan);
                setReadyForPurge = new ToggleWriter(
                    new BinaryReader(setReadyForPurgeStream, Encoding.ASCII),
                    new BinaryWriter(setReadyForPurgeStream),
                    READY_FOR_PURGE_FLAG);

                var setPurgeCompleteStream = new FileStream(WriteToFilename, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, 1, FileOptions.SequentialScan);
                setPurgeComplete = new ToggleWriter(
                    new BinaryReader(setPurgeCompleteStream, Encoding.ASCII),
                    new BinaryWriter(setPurgeCompleteStream),
                    PURGE_COMPLETE_FLAG);

                var ms = new MemoryStream();
                var msWriter = new BinaryWriter(ms);

                fileStream.Seek(MESSAGE_WRITE_POS, SeekOrigin.Begin);

                foreach (var command in SendQueue.GetConsumingEnumerable(cancellationTokenSource.Token))
                {
                    ms.SetLength(0);
                    command.Serialise(msWriter);

                    if (fileStream.Position + ms.Length >= Program.SHARED_FILE_SIZE - 10)
                    {
                        Program.Log($"[{writeFileShortName}] Instructing counterpart to prepare for purge.");

                        var purge = new Purge();
                        purge.Serialise(binaryWriter);

                        isReadyForPurge?.Wait();
                        //Program.Log($"[{writeFileShortName}] Counterpart is now ready for purge of {Path.GetFileName(WriteToFilename)}.");

                        fileStream.Seek(MESSAGE_WRITE_POS, SeekOrigin.Begin);
                        fileStream.SetLength(MESSAGE_WRITE_POS);

                        setPurgeComplete.Toggle();
                        Program.Log($"[{writeFileShortName}] Purge complete.");
                    }

                    //write the message to file
                    var commandStartPos = fileStream.Position;
                    command.Serialise(binaryWriter);
                    var commandEndPos = fileStream.Position;

                    //Program.Log($"[{writeFileShortName}] Wrote packet number {command.PacketNumber:N0} ({command.GetType().Name}) to position {commandStartPos:N0} - {commandEndPos:N0} ({(commandEndPos - commandStartPos).BytesToString()})");

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
                Environment.Exit(1);
            }
        }

        ToggleReader? isReadyForPurge;
        ToggleReader? isPurgeComplete;

        readonly CancellationTokenSource cancellationTokenSource = new();
        public void ReceivePump()
        {
            var readFileShortName = Path.GetFileName(ReadFromFilename);

            while (true)
            {
                try
                {
                    FileStream? fileStream = null;
                    BinaryReader? binaryReader = null;

                    try
                    {
                        var fileAlreadyExisted = File.Exists(ReadFromFilename) && new FileInfo(ReadFromFilename).Length > 0;
                        if (!fileAlreadyExisted)
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
                        }

                        fileStream = new FileStream(ReadFromFilename, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

                        if (fileAlreadyExisted)
                        {
                            Program.Log($"[{readFileShortName}] already existed. Seeking to end ({fileStream.Length:N0})");
                            fileStream.Seek(0, SeekOrigin.End);
                        }
                        else
                        {
                            fileStream.Seek(MESSAGE_WRITE_POS, SeekOrigin.Begin);
                        }

                        binaryReader = new BinaryReader(fileStream, Encoding.ASCII);

                        var isReadyForPurgeStream = new FileStream(ReadFromFilename, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, 1, FileOptions.SequentialScan);
                        isReadyForPurge = new ToggleReader(
                            new BinaryReader(isReadyForPurgeStream, Encoding.ASCII),
                            READY_FOR_PURGE_FLAG);

                        var isPurgeCompleteStream = new FileStream(ReadFromFilename, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, 1, FileOptions.SequentialScan);
                        isPurgeComplete = new ToggleReader(
                            new BinaryReader(isPurgeCompleteStream, Encoding.ASCII),
                            PURGE_COMPLETE_FLAG);
                    }
                    catch (Exception ex)
                    {
                        Program.Log($"[{readFileShortName}] Establish file: {ex}");
                        Environment.Exit(1);
                        return;
                    }


                    while (true)
                    {
                        while (true)
                        {
                            var nextByte = binaryReader.PeekChar();
                            if (nextByte != -1 && nextByte != 0)
                            {
                                break;
                            }

                            fileStream.Flush();     //force read

                            if (fileStream.Position > fileStream.Length)
                            {
                                throw new Exception($"[{readFileShortName}] has been restarted.");
                            }

                            //Program.Log($"[{readFileShortName}] waiting for data at position {fileStream.Position:N0}.")

                            WindowsDelay.Wait(1);
                        }

                        var commandStartPos = fileStream.Position;
                        var command = Command.Deserialise(binaryReader);
                        var commandEndPos = fileStream.Position;

                        if (command == null)
                        {
                            Program.Log($"[{readFileShortName}] Could not read command at file position {commandStartPos:N0}. [{ReadFromFilename}]", ConsoleColor.Red);
                            Environment.Exit(1);
                        }

                        //Program.Log($"[{readFileShortName}] Received packet number {command.PacketNumber:N0} ({command.GetType().Name}) from position {commandStartPos:N0} - {commandEndPos:N0} ({(commandEndPos - commandStartPos).BytesToString()})");

                        if (command is Forward forward && forward.Payload != null)
                        {
                            if (!ReceiveQueue.TryGetValue(forward.ConnectionId, out BlockingCollection<byte[]>? connectionReceiveQueue))
                            {
                                connectionReceiveQueue = new();
                                ReceiveQueue.Add(forward.ConnectionId, connectionReceiveQueue);
                            }

                            connectionReceiveQueue.Add(forward.Payload);

                            var totalBytesReceived = receivedBandwidth.TotalBytesTransferred + (ulong)(forward.Payload.Length);
                            receivedBandwidth.SetTotalBytesTransferred(totalBytesReceived);
                        }
                        else if (command is Connect connect)
                        {
                            if (!ReceiveQueue.ContainsKey(connect.ConnectionId))
                            {
                                var connectionReceiveQueue = new BlockingCollection<byte[]>();
                                ReceiveQueue.Add(connect.ConnectionId, connectionReceiveQueue);

                                var sharedFileStream = new SharedFileStream(this, connect.ConnectionId);
                                StreamEstablished?.Invoke(this, sharedFileStream);
                            }
                        }
                        else if (command is Purge)
                        {
                            Program.Log($"[{readFileShortName}] Counterpart is about to purge this file.");

                            //signal that we're ready
                            setReadyForPurge?.Toggle();
                            //Program.Log($"[{readFileShortName}] Informed counterpart we are ready for purge.");

                            //wait for the purge to be complete
                            //Program.Log($"[{readFileShortName}] Waiting for purge to be complete.");
                            isPurgeComplete.Wait();

                            Program.Log($"[{readFileShortName}] Purge is complete.");

                            fileStream.Seek(MESSAGE_WRITE_POS, SeekOrigin.Begin);
                            fileStream.Flush(); //force re-read of file
                        }
                        else if (command is TearDown teardown && ReceiveQueue.TryGetValue(teardown.ConnectionId, out BlockingCollection<byte[]>? connectionReceiveQueue))
                        {
                            Program.Log($"[{readFileShortName}] Counterpart asked to tear down connection {teardown.ConnectionId}");

                            ReceiveQueue.Remove(teardown.ConnectionId);

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
                                SendQueue.Add(response);
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
                }
            }
        }

        public override void Stop()
        {
            /*
            ConnectionIds
                .ForEach(connectionId =>
                {
                    var teardownCommand = new TearDown(connectionId);
                    SendQueue.Add(teardownCommand);
                });

            cancellationTokenSource.Cancel();
            receiveTask.Wait();
            sendTask.Wait();

            try
            {
                Program.Log($"Deleting {ReadFromFilename}");
                File.Delete(ReadFromFilename);
            }
            catch { }

            try
            {
                Program.Log($"Deleting {WriteToFilename}");
                File.Delete(WriteToFilename);
            }
            catch { }
            */
        }

        public string WriteToFilename { get; }
        public string ReadFromFilename { get; }
    }
}
