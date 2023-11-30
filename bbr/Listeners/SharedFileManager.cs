using bbr.Commands;
using bbr.Listeners;
using bbrelay.Utilities;
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

        const long PURGE_SIZE_BYTES = 10 * 1024 * 1024;

        public void SendPump()
        {
            StreamWriter streamWriter = null;

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
                            Share = FileShare.Read | FileShare.Delete,
                            //Options = FileOptions.WriteThrough | FileOptions.DeleteOnClose
                        });

                        if (!ConnectionIds.Contains(toSend.ConnectionId))
                        {
                            ConnectionIds.Add(toSend.ConnectionId);

                            var connectCommand = new Connect(toSend.ConnectionId);
                            var connectCommandStr = connectCommand.Serialize();

                            streamWriter.WriteLine(connectCommandStr);
                            streamWriter.Flush();
                        }


                        var commandStr = toSend.Serialize();

                        streamWriter.WriteLine(commandStr);
                        streamWriter.Flush();

                        if (streamWriter.BaseStream.Length > PURGE_SIZE_BYTES)
                        {
                            //tell the other side to purge the file
                            var purgeCommand = new Purge(toSend.ConnectionId);
                            var purgeCommandStr = purgeCommand.Serialize();

                            streamWriter.WriteLine(purgeCommandStr);
                            streamWriter.Flush();
                            streamWriter.Close();

                            //wait until the receiver has processed this message (signified by the file being deleted)

                            while (true)
                            {
                                Program.Log($"Waiting for file to be purged: {WriteToFilename}");

                                try
                                {
                                    //if (streamWriter.BaseStream.Length == 0)
                                    if (!IOUtils.FileExists(WriteToFilename))
                                    {
                                        streamWriter = null;

                                        break;
                                    }

                                    Thread.Sleep(10);
                                }
                                catch (Exception ex)
                                {
                                    Program.Log($"CopyTo: {ex}");
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

        readonly CancellationTokenSource cancellationTokenSource = new();
        public void ReceivePump()
        {
            StreamReader streamReader = null;

            var repeatCurrentLine = false;

            var lastSuccessfulLineEndPos = 0L;
            string line = null;
            while (!cancellationTokenSource.IsCancellationRequested)
            {
                if (streamReader == null)
                {
                    //wait for the file to exist
                    Program.Log($"Waiting for file to be created: {ReadFromFilename}");

                    var hadToWait = false;
                    while (!IOUtils.FileExists(ReadFromFilename))
                    {
                        hadToWait = true;
                        Thread.Sleep(10);
                    }

                    streamReader = new StreamReader(ReadFromFilename, new FileStreamOptions()
                    {
                        Mode = FileMode.Open,
                        Access = FileAccess.Read,
                        Share = FileShare.Read | FileShare.Write | FileShare.Delete
                    });

                    if (!hadToWait)
                    {
                        if (streamReader.BaseStream.Length > PURGE_SIZE_BYTES)
                        {
                            Program.Log($"Found existing file (and due to its size it must be purged): {ReadFromFilename}");

                            streamReader.Close();
                            var readingFromFile = streamReader.BaseStream as FileStream;
                            readingFromFile.Close();

                            File.Delete(ReadFromFilename);

                            streamReader = null;
                            continue;
                        }
                        else
                        {
                            //skip over the file's current content
                            string l;
                            do
                            {
                                l = streamReader.ReadLine();
                            } while (!string.IsNullOrEmpty(l));
                        }
                    }

                    Program.Log($"File has been created: {ReadFromFilename}");
                }

                var positionBeforeRead = 0L;
                var wasRepeat = false;
                if (repeatCurrentLine)
                {
                    //we're keeping the current line, to retry something
                    repeatCurrentLine = false;
                    wasRepeat = true;
                }
                else
                {
                    positionBeforeRead = streamReader.BaseStream.Position;
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
                        lastSuccessfulLineEndPos = streamReader.BaseStream.Position;

                        if (wasRepeat)
                        {
                            Program.Log($"After resetting stream, packet was {payload.Length:N0} bytes");
                        }
                    }
                    catch
                    {
                        Program.Log($"Couldn't convert base64 string from {payloadStr.Length:N0} characters, at position {positionBeforeRead:N0}");
                        Program.Log($"Resetting StreamReader.");

                        //FPS 02/02/2023: Unsure what causes this. The string is a subset of the whole line. If we do another ReadLine(), 2004 characters are skipped.
                        //For now, let's reset the StreamReader and jump to where we were.

                        
                        //streamReader.BaseStream.Position = lastSuccessfulLineEndPos;
                        //streamReader = new StreamReader(streamReader.BaseStream);
                        //streamReader.DiscardBufferedData();

                        var readingFromFile = streamReader.BaseStream as FileStream;
                        readingFromFile.Position = 0;
                        streamReader.DiscardBufferedData();

                        var originalLine = line;
                        while (true)
                        {
                            var positionBeforeReread = streamReader.BaseStream.Position;
                            line = streamReader.ReadLine();

                            if (line == null)
                            {
                                throw new Exception("Tried to recover from Base64 error, but could not.");
                            }

                            if (line.StartsWith(originalLine))
                            {
                                Program.Log($"Position after rescanning is: {positionBeforeReread:N0}. Delta is {positionBeforeReread - positionBeforeRead:N0} byes");
                                break;
                            }
                        }

                        Program.Log($"StreamReader reset.");

                        repeatCurrentLine = true;
                        continue;
                    }

                    var connectionReceiveQueue = ReceiveQueue[connectionId];
                    if (!connectionReceiveQueue.IsCompleted)
                    {
                        connectionReceiveQueue.Add(payload);
                    }
                }

                if (line.StartsWith("$purge"))
                {
                    Program.Log($"Was asked to purge {ReadFromFilename}");

                    //let's truncate the file, so that it doesn't get too big and to signify to the other side that we've processed it.
                    //FPS 30/11/2023: Occasionally, this doesn't seem to clear the file
                    /*
                    var readingFromFile = streamReader.BaseStream as FileStream;
                    readingFromFile.Position = 0;
                    readingFromFile.SetLength(0);
                    readingFromFile.Flush(true);
                    streamReader.DiscardBufferedData();
                    */

                    streamReader.Close();
                    var readingFromFile = streamReader.BaseStream as FileStream;
                    readingFromFile.Close();

                    File.Delete(ReadFromFilename);

                    streamReader = null;

                    Program.Log($"Purge complete: {ReadFromFilename}");
                }

                if (line.StartsWith("$teardown"))
                {
                    var tokens = line.Split('|');
                    var connectionId = tokens[1];

                    if (ConnectionIds.Contains(connectionId))
                    {
                        Program.Log($"Was asked to tear down {connectionId}");
                        var connectionReceiveQueue = ReceiveQueue[connectionId];
                        connectionReceiveQueue.CompleteAdding();

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
