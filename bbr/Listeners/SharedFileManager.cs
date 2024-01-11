using bbr.Commands;
using bbr.Listeners;
using bbrelay.Utilities;
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

namespace bbr.Streams
{
    public class SharedFileManager : StreamEstablisher
    {
        readonly Dictionary<int, BlockingCollection<byte[]>> ReceiveQueue = new();
        readonly BlockingCollection<Command> SendQueue = new();

        public SharedFileManager(string readFromFilename, string writeToFilename)
        {
            ReadFromFilename = readFromFilename;
            WriteToFilename = writeToFilename;

            Task.Factory.StartNew(ReceivePump);

            Task.Factory.StartNew(SendPump);
        }

        public byte[]? Read(int connectionId)
        {
            if (!ReceiveQueue.ContainsKey(connectionId))
            {
                ReceiveQueue.Add(connectionId, new BlockingCollection<byte[]>());
            }

            byte[]? result = null;
            if (ReceiveQueue.ContainsKey(connectionId))
            {
                var queue = ReceiveQueue[connectionId];

                try
                {
                    result = queue.Take(cancellationTokenSource.Token);
                }
                catch (InvalidOperationException)
                {
                    //This is normal - the queue might have been marked as AddingComplete while we were listening
                }
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
        }

        bool RemotePurgeUnderway = false;

        public const long PURGE_SIZE_BYTES = 1 * 1024 * 1024;

        public void SendPump()
        {
            try
            {
                FileStream? fileStream = null;
                BinaryWriter? writer = null;

                var firstStream = true;
                var fileAlreadyExisted = File.Exists(WriteToFilename);
                if (!fileAlreadyExisted)
                {
                    using var fs = File.Create(WriteToFilename);
                    Program.Log($"Created: {WriteToFilename}");
                }

                while (!cancellationTokenSource.IsCancellationRequested)
                {
                    try
                    {
                        foreach (var toSend in SendQueue.GetConsumingEnumerable(cancellationTokenSource.Token))
                        {
                            //FileOptions.DeleteOnClose causes access issues, and FileOptions.WriteThrough causes significant slowdown
                            if (fileStream == null)
                            {
                                fileStream = new FileStream(WriteToFilename, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);

                                if (fileAlreadyExisted && firstStream)
                                {
                                    fileStream.Seek(0, SeekOrigin.End);
                                    Program.Log($"Write file existed, so seeked to {fileStream.Position:N0} in {WriteToFilename}");
                                    firstStream = false;
                                }
                            }

                            writer ??= new BinaryWriter(fileStream, Encoding.UTF8, true);

                            toSend.Serialise(writer);
                            writer.Flush();

                            if (toSend is Forward forwardCommand)
                            {
                                Program.Log($"[Sent packet {forwardCommand.PacketNumber:N0}] [File position {fileStream.Position:N0}] [{forwardCommand.GetType().Name}] [{forwardCommand.Payload?.Length ?? 0:N0} bytes]");
                            }
                            else
                            {
                                Program.Log($"[Sent packet {toSend.PacketNumber:N0}] [File position {fileStream.Position:N0}] [{toSend.GetType().Name}]");
                            }

                            if (fileStream.Length > PURGE_SIZE_BYTES && toSend is Forward forward)
                            {
                                RemotePurgeUnderway = true;

                                //tell the other side to purge the file
                                var purgeCommand = new Purge(forward.ConnectionId);
                                purgeCommand.Serialise(writer);
                                writer.Flush();

                                Program.Log($"Asked other side to purge: {WriteToFilename}");

                                writer.Close();
                                writer = null;

                                fileStream.Close();
                                fileStream = null;

                                //This approach is fast, but occassionally the file is not empty.
                                /*
                                fileStream = new FileStream(WriteToFilename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

                                //wait until the receiver has processed this message (signified by the file being truncated)

                                var fileInfo = new FileInfo(WriteToFilename);
                                while (true)
                                {
                                    Program.Log($"Waiting for file to be purged: {WriteToFilename}");

                                    try
                                    {
                                        if (fileStream.Length == 0 && fileInfo.Length == 0)
                                        {
                                            break;
                                        }

                                        fileInfo.Refresh();
                                        Delay.Wait(1);
                                    }
                                    catch (Exception ex)
                                    {
                                        Program.Log($"Waiting for file to be purged: {ex}");
                                        break;
                                    }
                                }
                                fileStream = null;
                                */



                                //This approach is slow, but reliable
                                /*
                                var fileInfo = new FileInfo(WriteToFilename);
                                while (true)
                                {
                                    Program.Log($"Waiting for file to be purged: {WriteToFilename}");

                                    if (fileInfo.Length == 0)
                                    {
                                        Program.Log($"File is now empty: {WriteToFilename}");
                                        break;
                                    }

                                    fileInfo.Refresh();
                                    Delay.Wait(1);
                                }
                                */

                                var waitStart = DateTime.Now;
                                //todo - change this to something faster using WaitOne() (Semaphore? ManualResetEvent? AutoResetEvent?)
                                while (RemotePurgeUnderway)
                                {
                                    Delay.Wait(1);
                                    Program.Log($"SendPump() Waiting for other side to purge: {WriteToFilename}");

                                    var waitDuration = DateTime.Now - waitStart;
                                    if (waitDuration.TotalSeconds > 3)  //todo: Use a better way of detecting deadlock
                                    {
                                        //likely both sides are waiting
                                        RemotePurgeUnderway = false;
                                        break;
                                    }
                                }

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
            catch (Exception ex)
            {
                Program.Log(ex.ToString());
                Environment.Exit(1);
            }
        }

        public ulong TotalBytesReceived = 0;
        public DateTime started = DateTime.Now;

        readonly CancellationTokenSource cancellationTokenSource = new();
        public void ReceivePump()
        {
            try
            {
                FileStream? fileStream = null;
                BinaryReader? binaryReader = null;

                var firstStream = true;
                if (File.Exists(ReadFromFilename))
                {
                    using var fs = File.Create(ReadFromFilename);
                    Program.Log($"Created: {ReadFromFilename}");
                }

                while (true)
                {
                    if (fileStream == null)
                    {
                        fileStream = new FileStream(ReadFromFilename, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);

                        if (firstStream)
                        {
                            fileStream.Seek(0, SeekOrigin.End);
                            Program.Log($"Read file existed, so seeked to {fileStream.Position:N0} in {ReadFromFilename}");
                            firstStream = false;
                        }
                    }

                    binaryReader ??= new BinaryReader(fileStream);

                    while (binaryReader.PeekChar() == -1)
                    {
                        Delay.Wait(1);  //avoids a tight loop
                    }

                    var posBeforeCommand = fileStream.Position;

                    var command = Command.Deserialise(binaryReader);

                    if (command == null)
                    {
                        Program.Log($"Could not read command at file position {posBeforeCommand:N0}. [{ReadFromFilename}]", ConsoleColor.Red);
                        Environment.Exit(1);
                    }

                    if (command is Forward fwd)
                    {
                        TotalBytesReceived += (ulong)(fwd.Payload?.Length ?? 0);

                        var rate = TotalBytesReceived * 8 / (double)(DateTime.Now - started).TotalSeconds;

                        var ordinals = new[] { "", "K", "M", "G", "T", "P", "E" };
                        var ordinal = 0;
                        while (rate > 1024)
                        {
                            rate /= 1024;
                            ordinal++;
                        }
                        var bw = Math.Round(rate, 2, MidpointRounding.AwayFromZero);
                        var bwStr = $"{bw} {ordinals[ordinal]}b/s";

                        Program.Log($"[Received packet {fwd.PacketNumber:N0}] [File position {fileStream.Position:N0}] [{fwd.GetType().Name}] [{fwd.Payload?.Length ?? 0:N0} bytes] [{bwStr}]");
                    }
                    else
                    {
                        Program.Log($"[Received packet {command.PacketNumber:N0}] [File position {fileStream.Position:N0}] {command.GetType().Name}");
                    }

                    if (command is Forward forward)
                    {
                        if (!ReceiveQueue.ContainsKey(forward.ConnectionId))
                        {
                            ReceiveQueue.Add(forward.ConnectionId, new BlockingCollection<byte[]>());
                        }

                        var connectionReceiveQueue = ReceiveQueue[forward.ConnectionId];
                        if (forward.Payload != null)
                        {
                            connectionReceiveQueue.Add(forward.Payload);
                        }
                    }
                    else if (command is Connect connect)
                    {
                        if (!ReceiveQueue.ContainsKey(connect.ConnectionId))
                        {
                            ReceiveQueue.Add(connect.ConnectionId, new BlockingCollection<byte[]>());

                            var sharedFileStream = new SharedFileStream(this, connect.ConnectionId);
                            StreamEstablished?.Invoke(this, sharedFileStream);
                        }
                    }
                    else if (command is Purge purge)
                    {
                        Program.Log($"Was asked to purge connection {purge.ConnectionId} {ReadFromFilename}");

                        binaryReader.Close();
                        fileStream.Close();

                        binaryReader = null;
                        fileStream = null;

                        IOUtils.TruncateFile(ReadFromFilename);

                        var purgeComplete = new PurgeComplete();
                        SendQueue.Add(purgeComplete);
                        Program.Log($"Informed other side that purge is complete: {ReadFromFilename}");
                    }
                    else if (command is PurgeComplete)
                    {
                        Program.Log($"Informed by other side that purge is complete: {ReadFromFilename}");
                        RemotePurgeUnderway = false;
                    }
                    else if (command is TearDown teardown && ReceiveQueue.ContainsKey(teardown.ConnectionId))
                    {
                        Program.Log($"Was asked to tear down connection {teardown.ConnectionId}");
                        var connectionReceiveQueue = ReceiveQueue[teardown.ConnectionId];

                        ReceiveQueue.Remove(teardown.ConnectionId);

                        connectionReceiveQueue.CompleteAdding();
                    }
                }
            }
            catch (Exception ex)
            {
                Program.Log(ex.ToString());
                Environment.Exit(1);
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
