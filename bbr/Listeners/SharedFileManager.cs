using bbr.Commands;
using bbr.Listeners;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
        readonly List<string> FirstSends = new();

        public SharedFileManager(string readFromFilename, string writeToFilename)
        {
            ReadFromFilename = readFromFilename;
            WriteToFilename = writeToFilename;

            if (!string.IsNullOrEmpty(readFromFilename))
            {
                receiveTask = Task.Factory.StartNew(() =>
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
                sendTask = Task.Factory.StartNew(() =>
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

        readonly Task receiveTask;
        readonly Task sendTask;

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

        public void SendPump()
        {
            Stream fileStream;
            StreamWriter streamWriter = null;

            while (!cancellationTokenSource.IsCancellationRequested)
            {
                try
                {
                    foreach (var toSend in SendQueue.GetConsumingEnumerable(cancellationTokenSource.Token))
                    {
                        if (streamWriter == null)
                        {
                            fileStream = File.Open(WriteToFilename, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                            streamWriter = new StreamWriter(fileStream);
                        }

                        if (!FirstSends.Contains(toSend.ConnectionId))
                        {
                            FirstSends.Add(toSend.ConnectionId);

                            var connectCommand = new Connect(toSend.ConnectionId);
                            var connectCommandStr = connectCommand.Serialize();

                            streamWriter.WriteLine(connectCommandStr);
                            streamWriter.Flush();
                        }


                        var commandStr = toSend.Serialize();

                        streamWriter.WriteLine(commandStr);
                        streamWriter.Flush();

                        if (streamWriter.BaseStream.Length > 1024 * 1024)
                        {
                            //wait until the receiver has processed this message (signified by the file returning to zero bytes)
                            var purgeCommand = new Purge(toSend.ConnectionId);
                            var purgeCommandStr = purgeCommand.Serialize();

                            streamWriter.WriteLine(purgeCommandStr);
                            streamWriter.Flush();

                            Program.Log($"Waiting for file to be purged: {WriteToFilename}");
                            while (true)
                            {
                                try
                                {
                                    if (streamWriter.BaseStream.Length == 0)
                                    {
                                        //streamWriter.BaseStream.Position = 0;
                                        streamWriter = null;

                                        break;
                                    }

                                    Thread.Sleep(10);
                                }
                                catch
                                {
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
            }
        }

        readonly CancellationTokenSource cancellationTokenSource = new();
        public void ReceivePump()
        {
            FileStream fileStream;
            StreamReader streamReader;

            if (File.Exists(ReadFromFilename))
            {
                fileStream = File.Open(ReadFromFilename, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
                streamReader = new StreamReader(fileStream);

                //skip over the file's current content
                string l;
                do
                {
                    l = streamReader.ReadLine();
                } while (!string.IsNullOrEmpty(l));
            }
            else
            {
                //wait for the file to exist
                Program.Log($"Waiting for file to be created: {ReadFromFilename}");

                while (!File.Exists(ReadFromFilename))
                {
                    Thread.Sleep(100);
                }

                Program.Log($"File has been created: {ReadFromFilename}");

                fileStream = File.Open(ReadFromFilename, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
                streamReader = new StreamReader(fileStream);
            }

            var repeatCurrentLine = false;

            string line = null;
            while (!cancellationTokenSource.IsCancellationRequested)
            {
                if (repeatCurrentLine)
                {
                    //we're keeping the current line, to retry something
                    repeatCurrentLine = false;
                }
                else
                {
                    line = streamReader.ReadLine();
                }

                if (string.IsNullOrEmpty(line))
                {
                    Thread.Sleep(10);
                    continue;
                }

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
                    var connectionId = tokens[1];

                    if (!ReceiveQueue.ContainsKey(connectionId))
                    {
                        ReceiveQueue.Add(connectionId, new BlockingCollection<byte[]>());

                        var sharedFileStream = new SharedFileStream(this, connectionId);
                        StreamEstablished?.Invoke(this, sharedFileStream);
                    }

                    var payloadStr = tokens[2];

                    byte[] payload;
                    try
                    {
                        payload = Convert.FromBase64String(payloadStr);
                    }
                    catch
                    {
                        Program.Log($"Couldn't convert base64 string: {payloadStr}");
                        Program.Log($"Resetting StreamReader.");
                        //FPS 02/02/2023: Unsure what causes this. The string is a subset of the whole line. If we do another ReadLine(), 2004 characters are skipped.
                        //For now, let's reset the StreamReader and jump to where we were.

                        var readingFromFile = streamReader.BaseStream as FileStream;
                        readingFromFile.Position = 0;
                        streamReader.DiscardBufferedData();

                        var originalLine = line;
                        while (true)
                        {
                            line = streamReader.ReadLine();

                            if (line == null)
                            {
                                throw new Exception("Tried to recover from Base64 error, but could not.");
                            }

                            if (line.StartsWith(originalLine))
                            {
                                break;
                            }
                        }

                        Program.Log($"StreamReader reset.");

                        repeatCurrentLine = true;
                        continue;
                    }

                    var connectionReceiveQueue = ReceiveQueue[connectionId];
                    connectionReceiveQueue.Add(payload);
                }

                if (line.StartsWith("$purge"))
                {
                    //let's truncate the file, so that it doesn't get too big and to signify to the other side that we've processed it.
                    var readingFromFile = streamReader.BaseStream as FileStream;
                    readingFromFile.Position = 0;
                    readingFromFile.SetLength(0);
                    readingFromFile.Flush();
                    streamReader.DiscardBufferedData();
                }
            }
        }

        public override void Stop()
        {
            /*
            try
            {
                FirstSends
                    .ForEach(connectionId =>
                    {
                        var teardown = new TearDown(connectionId);
                        SendQueue.Add(teardown);
                    });
            }
            catch { }
            */

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
        }

        public string WriteToFilename { get; }
        public string ReadFromFilename { get; }
    }
}
