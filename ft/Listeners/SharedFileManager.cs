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
        readonly BlockingCollection<Ping> pingResponses = [];
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
                    var pingResponse = pingResponses.GetConsumingEnumerable().First();
                    string? pingDurationStr = null;
                    //if (pingResponse.ResponseToPacketNumber == pingRequest.PacketNumber)
                    {
                        pingStopwatch.Stop();
                        pingDurationStr = $"RTT: {pingStopwatch.ElapsedMilliseconds:N0} ms";
                    }
                    //else
                    //{
                    //    Program.Log($"Unexpected ping response. Expected: {pingRequest.PacketNumber:N0}, received: {pingResponse.ResponseToPacketNumber:N0}");
                    //}



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
            if (!ReceiveQueue.TryGetValue(connectionId, out BlockingCollection<byte[]>? queue))
            {
                queue = [];
                ReceiveQueue.Add(connectionId, queue);
            }

            byte[]? result = null;
            try
            {
                result = queue.Take(cancellationTokenSource.Token);
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
            try
            {
                //the writer always creates the file
                var fileStream = new FileStream(WriteToFilename, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite, Program.SHARED_FILE_SIZE * 2); //large buffer to prevent FileStream from autoflushing
                fileStream.SetLength(Program.SHARED_FILE_SIZE);

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

                foreach (var message in SendQueue.GetConsumingEnumerable(cancellationTokenSource.Token))
                {
                    ms.SetLength(0);
                    message.Serialise(msWriter);

                    if (fileStream.Position + ms.Length >= fileStream.Length - 10)
                    {
                        var purge = new Purge();
                        purge.Serialise(binaryWriter);
                        binaryWriter.Flush();

                        Program.Log($"Waiting for other side to be ready for purge.");
                        isReadyForPurge?.Wait();
                        Program.Log($"Other side is now ready for purge of {Path.GetFileName(WriteToFilename)}.");

                        fileStream.Seek(MESSAGE_WRITE_POS, SeekOrigin.Begin);
                        fileStream.WriteByte(0);    //command not ready
                        fileStream.Flush();

                        fileStream.Seek(MESSAGE_WRITE_POS, SeekOrigin.Begin);

                        setPurgeComplete.Toggle();
                        Program.Log($"Purge is now complete: {Path.GetFileName(WriteToFilename)}.");
                    }

                    //write the message to file
                    message.Serialise(binaryWriter);

                    if (message is Forward forward && forward.Payload != null)
                    {
                        var totalBytesSent = sentBandwidth.TotalBytesTransferred + (ulong)forward.Payload.Length;
                        sentBandwidth.SetTotalBytesTransferred(totalBytesSent);
                    }
                }
            }
            catch (Exception ex)
            {
                Program.Log(ex.ToString());
                Environment.Exit(1);
            }
        }

        ToggleReader? isReadyForPurge;
        ToggleReader? isPurgeComplete;

        readonly CancellationTokenSource cancellationTokenSource = new();
        public void ReceivePump()
        {
            while (true)
            {
                try
                {
                    FileStream? fileStream = null;
                    BinaryReader? binaryReader = null;

                    try
                    {
                        while (true)
                        {
                            if (File.Exists(ReadFromFilename) && new FileInfo(ReadFromFilename).Length > 0)
                            {
                                break;
                            }
                            Thread.Sleep(200);
                        }

                        fileStream = new FileStream(ReadFromFilename, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
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
                        Program.Log(ex.ToString());
                        Environment.Exit(1);
                        return;
                    }

                    fileStream.Seek(MESSAGE_WRITE_POS, SeekOrigin.Begin);

                    while (true)
                    {
                        while (true)
                        {
                            var nextByte = binaryReader.PeekChar();
                            if (nextByte != 0)
                            {
                                break;
                            }

                            fileStream.Flush();     //force read
                            WindowsDelay.Wait(1);
                        }

                        var posBeforeCommand = fileStream.Position;

                        var command = Command.Deserialise(binaryReader) ?? throw new Exception($"Could not read command at file position {posBeforeCommand:N0}. [{ReadFromFilename}]");

                        //Program.Log($"Received {command.GetType().Name}");

                        if (command is Forward forward && forward.Payload != null)
                        {
                            if (!ReceiveQueue.TryGetValue(forward.ConnectionId, out BlockingCollection<byte[]>? connectionReceiveQueue))
                            {
                                connectionReceiveQueue = [];
                                ReceiveQueue.Add(forward.ConnectionId, connectionReceiveQueue);
                            }

                            connectionReceiveQueue.Add(forward.Payload);

                            var totalBytesReceived = receivedBandwidth.TotalBytesTransferred + (ulong)(forward.Payload.Length);
                            receivedBandwidth.SetTotalBytesTransferred(totalBytesReceived);


                            //Wait for the data to be sent to the real server, making the connection synchronous
                            //while (connectionReceiveQueue.Count > 0 && ReceiveQueue.ContainsKey(forward.ConnectionId))
                            //{
                            //    Delay.Wait(1);
                            //}
                        }
                        else if (command is Connect connect)
                        {
                            if (!ReceiveQueue.ContainsKey(connect.ConnectionId))
                            {
                                ReceiveQueue.Add(connect.ConnectionId, []);

                                var sharedFileStream = new SharedFileStream(this, connect.ConnectionId);
                                StreamEstablished?.Invoke(this, sharedFileStream);
                            }
                        }
                        else if (command is Purge)
                        {
                            Program.Log($"Was asked to prepare for purge.");

                            //signal that we're ready
                            setReadyForPurge?.Toggle();
                            Program.Log($"Informed counterpart we are ready for purge.");

                            //wait for the purge to be complete
                            Program.Log($"Waiting for purge to be complete.");
                            isPurgeComplete.Wait();

                            Program.Log($"Informed purge is now complete. Resuming as normal.");
                            fileStream.Seek(MESSAGE_WRITE_POS, SeekOrigin.Begin);
                        }
                        else if (command is TearDown teardown && ReceiveQueue.TryGetValue(teardown.ConnectionId, out BlockingCollection<byte[]>? connectionReceiveQueue))
                        {
                            Program.Log($"Was asked to tear down connection {teardown.ConnectionId}");

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
                                pingResponses.Add(ping);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Program.Log($"{nameof(ReceivePump)}: {ex.Message}");
                    Program.Log($"Restarting {nameof(ReceivePump)}");
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
