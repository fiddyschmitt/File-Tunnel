using ft.Commands;
using ft.IO.Files;
using ft.Streams;
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace ft.Listeners
{
    public class UploadDownload : SharedFileManager
    {
        private readonly int maxFileSizeBytes;
        private readonly int readDurationMilliseconds;
        private readonly bool checkFileSizeAfterUpload;
        private readonly IFileAccess fileAccess;

        public UploadDownload(
                    IFileAccess fileAccess,
                    string readFromFilename,
                    string writeToFilename,
                    int maxFileSizeBytes,
                    int readDurationMilliseconds,
                    int tunnelTimeoutMilliseconds,
                    bool checkFileSizeAfterUpload,
                    bool verbose) : base(readFromFilename, writeToFilename, tunnelTimeoutMilliseconds, verbose)
        {
            this.fileAccess = fileAccess;
            this.maxFileSizeBytes = maxFileSizeBytes;
            this.readDurationMilliseconds = readDurationMilliseconds;
            this.checkFileSizeAfterUpload = checkFileSizeAfterUpload;
        }

        public override void SendPump()
        {
            var writeFileShortName = Path.GetFileName(WriteToFilename);
            var writeFileTempName = WriteToFilename + ".preparing";
            var writingStopwatch = new Stopwatch();

            try
            {
                if (fileAccess.Exists(writeFileTempName))
                {
                    fileAccess.Delete(writeFileTempName);
                }
            }
            catch { }

            try
            {
                if (fileAccess.Exists(WriteToFilename))
                {
                    fileAccess.Delete(WriteToFilename);
                }
            }
            catch { }

            while (true)
            {
                try
                {
                    using var memoryStream = new MemoryStream();
                    var hashingStream = new HashingStream(memoryStream);
                    var binaryWriter = new BinaryWriter(hashingStream);

                    var commandsWritten = 0;
                    //write as many commands as possible to the file
                    while (true)
                    {
                        int toWaitMillis;
                        if (commandsWritten == 0)
                        {
                            toWaitMillis = -1;  //wait indefinitely for the first command
                        }
                        else
                        {
                            toWaitMillis = (int)Math.Max(0L, readDurationMilliseconds - writingStopwatch.ElapsedMilliseconds);
                        }

                        if (SendQueue.TryTake(out Command? command, toWaitMillis))
                        {
                            if (commandsWritten == 0)
                            {
                                writingStopwatch.Restart();
                            }

                            //write the message to file
                            var commandStartPos = memoryStream.Position;
                            command.Serialise(binaryWriter);
                            var commandEndPos = memoryStream.Position;
                            commandsWritten++;

                            if (Verbose)
                            {
                                Program.Log($"[{writeFileShortName}] [{commandsWritten:N0}] Wrote packet number {command.PacketNumber:N0} ({command.GetName()}) to position {commandStartPos:N0} - {commandEndPos:N0} ({(commandEndPos - commandStartPos).BytesToString()})");
                            }

                            CommandSent(command);
                        }

                        if (SendQueue.Count == 0)
                        {
                            break;
                        }

                        if (memoryStream.Length >= maxFileSizeBytes)
                        {
                            break;
                        }

                        if (writingStopwatch.ElapsedMilliseconds >= readDurationMilliseconds)
                        {
                            break;
                        }
                    }

                    binaryWriter.Flush();

                    var memoryStreamContent = memoryStream.ToArray();

                    //write the file (sometimes it takes more than one go)
                    Extensions.Time(
                        $"[{writeFileShortName}] Write file content",
                        attempt =>
                        {
                            try
                            {
                                fileAccess.WriteAllBytes(writeFileTempName, memoryStreamContent);
                            }
                            catch (Exception ex)
                            {
                                if (Verbose)
                                {
                                    Program.Log($"[{writeFileShortName}] Write file content - attempt {attempt.Attempt:N0}]: {ex.Message}");
                                }
                            }
                        },
                        attempt =>
                        {
                            try
                            {
                                var result = fileAccess.Exists(writeFileTempName);

                                if (checkFileSizeAfterUpload)
                                {
                                    result &= fileAccess.GetFileSize(writeFileTempName) == memoryStreamContent.Length;
                                }

                                return result;
                            }
                            catch (Exception ex)
                            {
                                if (Verbose)
                                {
                                    Program.Log($"[{writeFileShortName}] [Confirm file was written - attempt {attempt.Attempt:N0}]: {ex.Message}");
                                }
                                return false;
                            }
                        },
                        DefaultSleepStrategy,
                        Verbose);


                    if (Verbose)
                    {
                        Program.Log($"[{writeFileShortName}] Wrote {commandsWritten:N0} commands in one transaction. {memoryStream.Length.BytesToString()}.");
                    }


                    //wait for the previous file to be deleted by counterpart, signaling it was processed
                    Extensions.Time(
                        $"[{writeFileShortName}] Wait for file to be deleted by counterpart",
                        _ => { },
                        attempt =>
                        {
                            bool result;
                            try
                            {
                                if (fileAccess is LocalAccess localAccess)
                                {
                                    //SMB slows down if both sides are using File.Exists on the same file.
                                    using var fs = File.Open(WriteToFilename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                                    result = true;
                                    fs.Close();
                                }
                                else
                                {
                                    result = fileAccess.Exists(WriteToFilename);
                                }
                            }
                            catch (Exception ex)
                            {
                                if (Verbose)
                                {
                                    Program.Log($"[{writeFileShortName}] [Wait for file to be deleted by counterpart - attempt {attempt.Attempt:N0}]: {ex.Message}");
                                }
                                result = false;
                            }

                            return !result;
                        },
                        DefaultSleepStrategy,
                        Verbose);




                    //move the file into place
                    var moved = 0;
                    Extensions.Time(
                        $"[{writeFileShortName}] Move file into place",
                        attempt =>
                        {
                            try
                            {
                                fileAccess.Move(writeFileTempName, WriteToFilename, true);
                                Interlocked.Increment(ref moved);
                                return;
                            }
                            catch (FileNotFoundException)
                            {
                                //try writing it again
                                try
                                {
                                    fileAccess.WriteAllBytes(writeFileTempName, memoryStreamContent);
                                }
                                catch (Exception ex)
                                {
                                    if (Verbose)
                                    {
                                        Program.Log($"[{writeFileShortName}] [Rewriting during move attempt {attempt.Attempt:N0}]: {ex.Message}");
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                if (Verbose)
                                {
                                    Program.Log($"[{writeFileShortName}] [Move file into place - attempt {attempt.Attempt:N0}]: {ex.Message}");
                                }
                            }
                        },
                        _ => moved == 1,
                        attempt =>
                        {
                            var sleep = DefaultSleepStrategy(attempt);

                            //rclone ftp mount throws "The request could not be performed because of an I/O device error" if file move requests are done in quick succession
                            sleep = Math.Max(sleep, 20);

                            return sleep;
                        },
                        Verbose);
                }
                catch (Exception ex)
                {
                    Program.Log($"[{writeFileShortName}] {nameof(SendPump)}: {ex}");
                    Program.Log($"[{writeFileShortName}] Restarting {nameof(SendPump)}");
                    Thread.Sleep(1000);
                }
            }
        }

        public override void ReceivePump()
        {
            var readFileShortName = Path.GetFileName(ReadFromFilename);

            try
            {
                if (fileAccess.Exists(ReadFromFilename))
                {
                    fileAccess.Delete(ReadFromFilename);
                }
            }
            catch { }

            while (true)
            {
                try
                {
                    Extensions.Time(
                        $"[{readFileShortName}] Wait for file to exist",
                        _ => { },
                        attempt =>
                        {
                            try
                            {
                                var result = fileAccess.Exists(ReadFromFilename);
                                return result;
                            }
                            catch (Exception ex)
                            {
                                if (Verbose)
                                {
                                    Program.Log($"[{ReadFromFilename}] [Wait for file to exist - attempt {attempt.Attempt:N0}]: {ex.Message}");
                                }
                                return false;
                            }
                        },
                        DefaultSleepStrategy,
                        Verbose);




                    byte[] fileContent = [];
                    Extensions.Time(
                        $"[{readFileShortName}] Read file contents",
                        attempt =>
                        {
                            try
                            {
                                fileContent = fileAccess.ReadAllBytes(ReadFromFilename);
                            }
                            catch (Exception ex)
                            {
                                if (Verbose)
                                {
                                    Program.Log($"[{readFileShortName}] [Read file contents - attempt {attempt.Attempt:N0}]: {ex.Message}");
                                }
                            }
                        },
                        _ => fileContent?.Length > 0,
                        DefaultSleepStrategy,
                        Verbose);


                    var deleted = 0;
                    Extensions.Time(
                        $"[{readFileShortName}] Delete processed file",
                        attempt =>
                        {
                            try
                            {
                                fileAccess.Delete(ReadFromFilename);
                                Interlocked.Increment(ref deleted);
                            }
                            catch (Exception ex)
                            {
                                if (Verbose)
                                {
                                    Program.Log($"[{readFileShortName}] [Delete processed file - attempt {attempt.Attempt:N0}]: {ex.Message}");
                                }
                            }
                        },
                        _ => { return deleted == 1; },
                        DefaultSleepStrategy,
                        Verbose);

                    using var memoryStream = new MemoryStream(fileContent);
                    var hashingStream = new HashingStream(memoryStream);
                    var binaryReader = new BinaryReader(hashingStream, Encoding.ASCII);

                    if (Verbose)
                    {
                        Program.Log($"[{readFileShortName}] Processing file content");
                    }

                    var commandsRead = 0;
                    while (memoryStream.Position < memoryStream.Length)
                    {
                        var commandStartPos = memoryStream.Position;
                        var command = Command.Deserialise(binaryReader);
                        var commandEndPos = memoryStream.Position;

                        if (command == null)
                        {
                            var exMsg = $"[{readFileShortName}] Could not read command at file position {commandStartPos:N0}.";
                            if (Verbose)
                            {
                                Program.Log(exMsg);
                            }
                            throw new Exception(exMsg);
                        }

                        if (Verbose)
                        {
                            Program.Log($"[{readFileShortName}] Received packet number {command.PacketNumber:N0} ({command.GetName()}) from position {commandStartPos:N0} - {commandEndPos:N0} ({(commandEndPos - commandStartPos).BytesToString()})");
                        }

                        CommandReceived(command);
                        commandsRead++;
                    }

                    if (Verbose)
                    {
                        Program.Log($"[{readFileShortName}] Read {commandsRead:N0} commands in one transaction. {memoryStream.Length.BytesToString()}.");
                    }

                    if (Verbose)
                    {
                        Program.Log($"[{readFileShortName}] Finished processing file content");
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

        int DefaultSleepStrategy((int Attempt, TimeSpan Elapsed, string Operation) attempt)
        {
            if (attempt.Elapsed.TotalMilliseconds > TunnelTimeoutMilliseconds)
            {
                throw new Exception($"{attempt.Operation} has exceeded the tunnel timeout of {TunnelTimeoutMilliseconds:N0} ms. Cancelling.");
            }

            if (attempt.Elapsed.TotalMilliseconds < 100) return 1;
            if (attempt.Elapsed.TotalMilliseconds < 1000) return 20;
            return 100;
        }

        public override void Stop(string reason)
        {
            Program.Log($"{nameof(UploadDownload)}: Stopping. Reason: {reason}");
        }
    }
}
