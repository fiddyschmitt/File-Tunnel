using bbr.Commands;
using bbr.Listeners;
using bbrelay.Utilities;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Mail;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace bbr.Streams
{
    public class SharedFileManager : StreamEstablisher
    {
        readonly Dictionary<string, BlockingCollection<byte[]>> ReceiveQueue = new();
        readonly BlockingCollection<Command> SendQueue = new();
        readonly List<string> ConnectionIds = new();

        public SharedFileManager(string readFromFilename, string writeToFilename)
        {
            ReadFromFilename = readFromFilename;
            WriteToFilename = writeToFilename;

            if (!string.IsNullOrEmpty(readFromFilename))
            {
                Task.Factory.StartNew(() =>
                {
                    try
                    {
                        ReceivePump();
                    }
                    catch (Exception ex)
                    {
                        Program.Log($"ReceivePump: {ex}");
                        Environment.Exit(1);
                    }
                });
            }

            if (!string.IsNullOrEmpty(writeToFilename))
            {
                Task.Factory.StartNew(() =>
                {
                    try
                    {
                        SendPump();
                    }
                    catch (Exception ex)
                    {
                        Program.Log($"SendPump: {ex}");
                        Environment.Exit(1);
                    }
                });
            }
        }

        public byte[] Read(string connectionId)
        {
            if (!ReceiveQueue.ContainsKey(connectionId))
            {
                ReceiveQueue.Add(connectionId, new BlockingCollection<byte[]>());
            }

            var queue = ReceiveQueue[connectionId];

            byte[] result = queue.Take(cancellationTokenSource.Token);
            return result;
        }

        public void Write(string connectionId, byte[] data)
        {
            var forwardCommand = new Forward(connectionId, data);
            SendQueue.Add(forwardCommand);
        }

        public void TearDown(string connectionId)
        {
            var teardownCommand = new TearDown(connectionId);
            SendQueue.Add(teardownCommand);
        }

        public const long PURGE_SIZE_BYTES = 1 * 1024 * 1024;

        static string wroteFilename = @$"\\192.168.1.31\e\Temp\bb\wrote-{Environment.MachineName}.txt";
        static bool logWrites = false;
        static bool logReads = false;
        public static void Send(string str, StreamWriter streamWriter)
        {
            if (logWrites) File.AppendAllText(wroteFilename, str.Length + Environment.NewLine);
            streamWriter.Write(str.Length + Environment.NewLine);

            if (logWrites) File.AppendAllText(wroteFilename, str + Environment.NewLine);
            streamWriter.WriteLine(str);

            streamWriter.Flush();
        }

        public void SendPump()
        {
            StreamWriter streamWriter = null;

            if (logWrites && File.Exists(wroteFilename)) File.Delete(wroteFilename);

            while (!cancellationTokenSource.IsCancellationRequested)
            {
                try
                {
                    foreach (var toSend in SendQueue.GetConsumingEnumerable(cancellationTokenSource.Token))
                    {
                        streamWriter ??= new StreamWriter(WriteToFilename, new FileStreamOptions()
                        {
                            Mode = FileMode.Append,
                            Access = FileAccess.Write,
                            Share = FileShare.ReadWrite | FileShare.Delete,
                            Options = FileOptions.None     //FileOptions.DeleteOnClose causes access issues, and FileOptions.WriteThrough causes significant slowdown
                        });

                        if (!ConnectionIds.Contains(toSend.ConnectionId))
                        {
                            ConnectionIds.Add(toSend.ConnectionId);

                            var connectCommand = new Connect(toSend.ConnectionId);
                            var connectCommandStr = connectCommand.Serialize();

                            Send(connectCommandStr, streamWriter);
                        }

                        var commandStr = toSend.Serialize();
                        Send(commandStr, streamWriter);

                        if (streamWriter.BaseStream.Length > PURGE_SIZE_BYTES)
                        {
                            //tell the other side to purge the file
                            var purgeCommand = new Purge(toSend.ConnectionId);
                            var purgeCommandStr = purgeCommand.Serialize();

                            Send(purgeCommandStr, streamWriter);

                            //wait until the receiver has processed this message (signified by the file being truncated)

                            while (true)
                            {
                                Program.Log($"Waiting for file to be purged: {WriteToFilename}");

                                try
                                {
                                    if (streamWriter.BaseStream.Length == 0)
                                    {
                                        break;
                                    }

                                    Thread.Sleep(10);
                                }
                                catch (Exception ex)
                                {
                                    Program.Log($"Waiting for file to be purged: {ex}");
                                    break;
                                }
                            }

                            streamWriter.BaseStream.Position = 0;

                            streamWriter = new StreamWriter(WriteToFilename, new FileStreamOptions()
                            {
                                Mode = FileMode.Append,
                                Access = FileAccess.Write,
                                Share = FileShare.ReadWrite | FileShare.Delete
                            });

                            Program.Log($"File purge is complete: {WriteToFilename}");
                        }
                    }
                }
                catch (OperationCanceledException)
                {

                }
                catch (Exception ex)
                {
                    Program.Log($"CopyTo: {ex}");
                }
            }
        }

        readonly CancellationTokenSource cancellationTokenSource = new();
        public void ReceivePump()
        {
            var readFilename = @$"\\192.168.1.31\e\Temp\bb\read-{Environment.MachineName}.txt";
            if (logReads && File.Exists(readFilename)) File.Delete(readFilename);

            foreach (var line in IOUtils.Tail(ReadFromFilename))
            {
                if (logReads) File.AppendAllText(readFilename, line + Environment.NewLine);

                if (line.StartsWith("$connect"))
                {
                    var tokens = line.Split('|');
                    var connectionId = tokens[1];

                    if (!ReceiveQueue.ContainsKey(connectionId))
                    {
                        ReceiveQueue.Add(connectionId, new BlockingCollection<byte[]>());

                        var sharedFileStream = new SharedFileStream(this, connectionId);
                        StreamEstablished?.Invoke(this, sharedFileStream);
                    }
                }

                if (line.StartsWith("$forward"))
                {
                    var tokens = line.Split('|');
                    var connectionId = tokens[2];

                    if (!ReceiveQueue.ContainsKey(connectionId))
                    {
                        ReceiveQueue.Add(connectionId, new BlockingCollection<byte[]>());

                        var sharedFileStream = new SharedFileStream(this, connectionId);
                        StreamEstablished?.Invoke(this, sharedFileStream);
                    }

                    var payloadStr = tokens[3];

                    var payload = Convert.FromBase64String(payloadStr);

                    var connectionReceiveQueue = ReceiveQueue[connectionId];
                    if (!connectionReceiveQueue.IsCompleted)
                    {
                        connectionReceiveQueue.Add(payload);
                    }
                }



                if (line.StartsWith("$teardown"))
                {
                    var tokens = line.Split('|');
                    var connectionId = tokens[1];

                    if (ConnectionIds.Contains(connectionId))
                    {
                        Program.Log($"Was asked to tear down {connectionId}");
                        var connectionReceiveQueue = ReceiveQueue[connectionId];
                        //connectionReceiveQueue.CompleteAdding();

                        ConnectionIds.Remove(connectionId);
                    }
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
