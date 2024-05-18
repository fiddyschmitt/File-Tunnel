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

                var bwStr = $"Read from {readFromFilename}: {receivedBandwidthStr,-25} Wrote to {writeToFilename}: {sentBandwidthStr}";
                Program.Log(bwStr);

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

        public void SendPump()
        {
            try
            {
                var fileStream = new FileStream(WriteToFilename, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite);
                fileStream.SetLength(Program.SHARED_FILE_SIZE);

                var fileWriter = new BinaryWriter(fileStream);
                var fileReader = new BinaryReader(fileStream, Encoding.ASCII);

                //write messages to disk
                foreach (var message in SendQueue.GetConsumingEnumerable(cancellationTokenSource.Token))
                {
                    //signal that the batch is not ready to read
                    fileStream.Seek(0, SeekOrigin.Begin);
                    fileWriter.Write((byte)0);

                    //write the message to file
                    message.Serialise(fileWriter);

                    //signal that the batch is ready
                    fileStream.Seek(0, SeekOrigin.Begin);
                    fileWriter.Write((byte)1);
                    fileWriter.Flush();

                    if (message is Forward forward && forward.Payload != null)
                    {
                        var totalBytesSent = sentBandwidth.TotalBytesTransferred + (ulong)forward.Payload.Length;
                        sentBandwidth.SetTotalBytesTransferred(totalBytesSent);
                    }

                    //wait for the counterpart to acknowledge the batch, by setting the first byte to zero
                    fileStream.Seek(0, SeekOrigin.Begin);
                    while (true)
                    {
                        var nextByte = fileReader.PeekChar();

                        if (nextByte == 0)
                        {
                            break;
                        }
                        else
                        {
                            //Console.WriteLine($"[{ReadFromFilename}] Waiting for data at position {fileStream.Position:N0}");
                            fileStream.Flush(); //force read from file

                            Delay.Wait(1);  //avoids a tight loop
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Program.Log(ex.ToString());
                Environment.Exit(1);
            }
        }

        readonly CancellationTokenSource cancellationTokenSource = new();
        public void ReceivePump()
        {
            FileStream? fileStream = null;
            BinaryReader? binaryReader = null;
            BinaryWriter? binaryWriter = null;

            try
            {
                fileStream = new FileStream(ReadFromFilename, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite);
                fileStream.SetLength(Program.SHARED_FILE_SIZE);

                binaryReader = new BinaryReader(fileStream, Encoding.ASCII);
                binaryWriter = new BinaryWriter(fileStream);
            }
            catch (Exception ex)
            {
                Program.Log(ex.ToString());
                Environment.Exit(1);
                return;
            }


            while (true)
            {
                try
                {
                    while (true)
                    {
                        var nextByte = binaryReader.PeekChar();

                        if (nextByte == -1 || nextByte == 0)
                        {
                            //Console.WriteLine($"[{ReadFromFilename}] Waiting for data at position {fileStream.Position:N0}");
                            fileStream.Flush(); //force read from file
                            Delay.Wait(1);  //avoids a tight loop
                        }
                        else
                        {
                            break;
                        }
                    }

                    //skip the byte that signalled that the batch was ready
                    fileStream.Seek(1, SeekOrigin.Current);


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
                    fileStream.Seek(0, SeekOrigin.Begin);
                    binaryWriter.Write((byte)0);
                    fileStream.Seek(0, SeekOrigin.Begin);

                }
                catch (FileNotFoundException fileNotFoundException)
                {
                    //This happens once in a while on network shares. So just try again
                    Program.Log(fileNotFoundException.ToString());
                    Program.Log("Retrying.");
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
