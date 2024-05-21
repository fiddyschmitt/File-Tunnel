using ft.Bandwidth;
using ft.Commands;
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
            Task.Factory.StartNew(ReportBandwidth, TaskCreationOptions.LongRunning);
        }

        const int reportIntervalMs = 1000;
        readonly BandwidthTracker sentBandwidth = new(100, reportIntervalMs);
        readonly BandwidthTracker receivedBandwidth = new(100, reportIntervalMs);
        public void ReportBandwidth()
        {
            var readFromFilename = Path.GetFileName(ReadFromFilename);
            var writeToFilename = Path.GetFileName(WriteToFilename);

            while (true)
            {
                var sentBandwidthStr = sentBandwidth.GetBandwidth();
                var receivedBandwidthStr = receivedBandwidth.GetBandwidth();

                var logStr = $"Read from file: {receivedBandwidthStr,-12} Wrote to file: {sentBandwidthStr,-12}";

                var fileLatencyWindow = DateTime.Now.AddMilliseconds(-reportIntervalMs);
                lock (fileLatencies)
                {
                    fileLatencies.RemoveAll(sample => sample.DateTime < fileLatencyWindow);
                    if (fileLatencies.Count > 0)
                    {
                        var avgFileLatency = fileLatencies.Average(rec => rec.LatencyMs);
                        logStr += $" File latency: {avgFileLatency:N0} ms ({fileLatencies.Count:N0} samples)";
                    }
                }

                Program.Log(logStr);

                Thread.Sleep(reportIntervalMs);
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

        readonly List<(DateTime DateTime, long LatencyMs)> fileLatencies = [];
        public void SendPump()
        {
            try
            {
                var fileStream = new FileStream(WriteToFilename, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite);
                fileStream.SetLength(Program.SHARED_FILE_SIZE);

                //write acks to file
                Task.Factory.StartNew(() =>
                {
                    var ackFileStream = new FileStream(WriteToFilename, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, 1, FileOptions.SequentialScan);
                    var ackWriter = new BinaryWriter(ackFileStream);
                    foreach (var ack in LocallyAckedPacketNumber.GetConsumingEnumerable())
                    {
                        ackWriter.Write(ack);
                        ackWriter.Flush();

                        ackWriter.Seek(0, SeekOrigin.Begin);
                    }

                }, TaskCreationOptions.LongRunning);

                var fileWriter = new BinaryWriter(fileStream);
                var fileReader = new BinaryReader(fileStream, Encoding.ASCII);

                var stopwatch = new Stopwatch();

                var remotelyAckedPacketNumberCE = RemotelyAckedPacketNumber.GetConsumingEnumerable();

                //write messages to disk
                foreach (var message in SendQueue.GetConsumingEnumerable(cancellationTokenSource.Token))
                {
                    //write the message to file
                    fileStream.Seek(sizeof(ulong), SeekOrigin.Begin);       //skip the remote ack's
                    fileStream.Seek(sizeof(ulong), SeekOrigin.Current);     //skip the 'ready' packet number
                    message.Serialise(fileWriter);

                    //signal that the message is ready
                    fileStream.Seek(sizeof(ulong), SeekOrigin.Begin);
                    fileWriter.Write(message.PacketNumber);

                    stopwatch.Restart();
                    fileWriter.Flush();

                    if (message is Forward forward && forward.Payload != null)
                    {
                        var totalBytesSent = sentBandwidth.TotalBytesTransferred + (ulong)forward.Payload.Length;
                        sentBandwidth.SetTotalBytesTransferred(totalBytesSent);
                    }

                    //wait for the counterpart to acknowledge the message
                    foreach (var remoteAck in remotelyAckedPacketNumberCE)
                    {
                        if (remoteAck == message.PacketNumber)
                        {
                            break;
                        }
                    }

                    stopwatch.Stop();
                    fileLatencies.Add((DateTime.Now, stopwatch.ElapsedMilliseconds));
                }
            }
            catch (Exception ex)
            {
                Program.Log(ex.ToString());
                Environment.Exit(1);
            }
        }

        readonly BlockingCollection<ulong> LocallyAckedPacketNumber = new(1);
        readonly BlockingCollection<ulong> RemotelyAckedPacketNumber = new(1);

        readonly CancellationTokenSource cancellationTokenSource = new();
        public void ReceivePump()
        {
            FileStream? fileStream = null;
            BinaryReader? binaryReader = null;

            try
            {
                using (fileStream = new FileStream(ReadFromFilename, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite))
                {
                    fileStream.SetLength(Program.SHARED_FILE_SIZE);
                }

                fileStream = new FileStream(ReadFromFilename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                binaryReader = new BinaryReader(fileStream, Encoding.ASCII);
            }
            catch (Exception ex)
            {
                Program.Log(ex.ToString());
                Environment.Exit(1);
                return;
            }

            //read acks from file
            Task.Factory.StartNew(() =>
            {
                var ackFileStream = new FileStream(ReadFromFilename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.SequentialScan);
                var ackReader = new BinaryReader(ackFileStream);

                var latestReadAck = 0UL;
                while (true)
                {
                    ackFileStream.Seek(0, SeekOrigin.Begin);
                    var newAck = ackReader.ReadUInt64();
                    if (latestReadAck != newAck)
                    {
                        latestReadAck = newAck;
                        RemotelyAckedPacketNumber.Add(newAck);
                    }

                    //ackFileStream.Flush();  //force read. (Not needed now because bufferSize = 1 and SequentialScan)

                    Delay.Wait(1);
                }
            }, TaskCreationOptions.LongRunning);

            var packetNumberFileStream = new FileStream(ReadFromFilename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.SequentialScan);
            var packetNumberReader = new BinaryReader(packetNumberFileStream);

            var expectedPacketNumber = 1UL;
            while (true)
            {
                try
                {
                    while (true)
                    {
                        var packetInFile = ulong.MaxValue;

                        try
                        {
                            packetNumberFileStream.Seek(sizeof(ulong), SeekOrigin.Begin);
                            packetInFile = packetNumberReader.ReadUInt64();

                            if (packetInFile != expectedPacketNumber)
                            {
                                //Program.Log($"[{ReadFromFilename}] In file: {packetInFile:N0}. Waiting for packet {expectedPacketNumber:N0}");
                                //packetNumberFileStream.Flush();   //force read. (Not needed now because bufferSize = 1 and SequentialScan)
                                Delay.Wait(1);  //avoids a tight loop
                            }
                            else
                            {
                                break;
                            }
                        }
                        catch (Exception)
                        {
                            Program.Log("Retrying read of packet number");
                        }
                    }

                    fileStream.Seek(sizeof(ulong), SeekOrigin.Begin);   //skip ack ulong
                    fileStream.Seek(sizeof(ulong), SeekOrigin.Current);   //skip packet number ulong

                    var posBeforeCommand = fileStream.Position;

                    var command = Command.Deserialise(binaryReader);

                    if (command == null)
                    {
                        Program.Log($"Could not read command at file position {posBeforeCommand:N0}. [{ReadFromFilename}]", ConsoleColor.Red);
                        Environment.Exit(1);
                    }

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


                        //Not working yet. Causes iperf to not finish correctly.
                        //wait for it to be sent to the real server, making the connection synchronous
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
                    else if (command is TearDown teardown && ReceiveQueue.TryGetValue(teardown.ConnectionId, out BlockingCollection<byte[]>? connectionReceiveQueue))
                    {
                        Program.Log($"Was asked to tear down connection {teardown.ConnectionId}");

                        ReceiveQueue.Remove(teardown.ConnectionId);

                        connectionReceiveQueue.CompleteAdding();
                    }


                    //signal that we have processed their message
                    LocallyAckedPacketNumber.Add(command.PacketNumber);

                    expectedPacketNumber = command.PacketNumber + 1;
                }
                catch (Exception ex)
                {
                    Program.Log(ex.ToString());
                    Environment.Exit(1);
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
