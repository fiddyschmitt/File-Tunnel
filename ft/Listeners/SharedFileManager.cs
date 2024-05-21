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
        public void ReportNetworkPerformance()
        {
            while (true)
            {
                try
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

        const int READY_TO_READ_FLAG_POS = 0;
        const int MESSAGE_PROCESSED_FLAG_POS = 1;
        const int MESSAGE_POS = 2;

        ToggleWriter? setReadyToRead;
        ToggleWriter? setMessageProcessed;

        readonly List<(DateTime DateTime, long LatencyMs)> fileLatencies = [];
        public void SendPump()
        {
            try
            {
                //the writer always creates the file
                var fileStream = new FileStream(WriteToFilename, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite, 1, FileOptions.SequentialScan);
                fileStream.SetLength(MESSAGE_POS);

                var binaryWriter = new BinaryWriter(fileStream);
                var binaryReader = new BinaryReader(fileStream, Encoding.ASCII);

                setReadyToRead = new ToggleWriter(
                    binaryReader,
                    binaryWriter,
                    READY_TO_READ_FLAG_POS);

                setMessageProcessed = new ToggleWriter(
                    binaryReader,
                    binaryWriter,
                    MESSAGE_PROCESSED_FLAG_POS);


                var stopwatch = new Stopwatch();

                foreach (var message in SendQueue.GetConsumingEnumerable(cancellationTokenSource.Token))
                {
                    //write the message to file
                    lock (fileStream)
                    {
                        fileStream.Seek(MESSAGE_POS, SeekOrigin.Begin);
                        message.Serialise(binaryWriter);

                        //signal that the message is ready
                        setReadyToRead.Toggle();

                        stopwatch.Restart();
                        binaryWriter.Flush();
                    }



                    if (message is Forward forward && forward.Payload != null)
                    {
                        var totalBytesSent = sentBandwidth.TotalBytesTransferred + (ulong)forward.Payload.Length;
                        sentBandwidth.SetTotalBytesTransferred(totalBytesSent);
                    }

                    //wait for the counterpart to acknowledge the message
                    isMessageProcessed?.Wait();

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

        ToggleReader? isReadyToRead;
        ToggleReader? isMessageProcessed;

        readonly CancellationTokenSource cancellationTokenSource = new();
        public void ReceivePump()
        {
            FileStream? fileStream = null;
            BinaryReader? binaryReader = null;

            while (true)
            {
                try
                {
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

                        fileStream = new FileStream(ReadFromFilename, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, 1, FileOptions.SequentialScan);
                        binaryReader = new BinaryReader(fileStream, Encoding.ASCII);

                        isReadyToRead = new ToggleReader(
                            binaryReader,
                            READY_TO_READ_FLAG_POS);

                        isMessageProcessed = new ToggleReader(
                            binaryReader,
                            MESSAGE_PROCESSED_FLAG_POS);

                    }
                    catch (Exception ex)
                    {
                        Program.Log(ex.ToString());
                        Environment.Exit(1);
                        return;
                    }

                    while (true)
                    {

                        //wait for the counterpart to signal that the message is ready to be read
                        isReadyToRead.Wait();

                        Command? command;
                        lock (fileStream)
                        {
                            fileStream.Seek(MESSAGE_POS, SeekOrigin.Begin);

                            command = Command.Deserialise(binaryReader) ?? throw new Exception($"Could not read command at file position {MESSAGE_POS:N0}. [{ReadFromFilename}]");
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


                            //Wait for the data to be sent to the real server, making the connection synchronous
                            while (connectionReceiveQueue.Count > 0 && ReceiveQueue.ContainsKey(forward.ConnectionId))
                            {
                                Delay.Wait(1);
                            }
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
                        setMessageProcessed?.Toggle();
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
