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
    public class WriteThenWaitForDelete : SharedFileManager
    {
        private readonly int maxFileSizeBytes;
        private readonly int readDurationMilliseconds;
        private readonly bool checkFileSizeAfterUpload;
        private readonly IFileAccess fileAccess;

        public WriteThenWaitForDelete(
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
            var writeFileTempName = WriteToFilename + ".tmp";
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
                        var command = SendQueue.Take();

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
                            Program.Log($"[{writeFileShortName}] Wrote packet number {command.PacketNumber:N0} ({command.GetName()}) to position {commandStartPos:N0} - {commandEndPos:N0} ({(commandEndPos - commandStartPos).BytesToString()})");
                        }

                        CommandSent(command);

                        if (SendQueue.Count == 0)
                        {
                            break;
                        }

                        if (memoryStream.Length >= maxFileSizeBytes)
                        {
                            break;
                        }

                        if (writingStopwatch.ElapsedMilliseconds > readDurationMilliseconds)
                        {
                            break;
                        }
                    }

                    binaryWriter.Flush();

                    var memoryStreamContent = memoryStream.ToArray();

                    //write the file (sometimes it takes more than one go)
                    Extensions.Time(
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
                                    Program.Log($"[{writeFileShortName}] [Write to file - attempt {attempt:N0}]: {ex.Message}");
                                }
                            }
                        },
                        () =>
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
                                    Program.Log($"[{writeFileShortName}] [Confirm file was written]: {ex.Message}");
                                }
                                return false;
                            }
                        },
                        _ => 1,
                        $"[{writeFileShortName}] Writing file content",
                        Verbose);


                    if (Verbose)
                    {
                        Program.Log($"[{writeFileShortName}] Wrote {commandsWritten:N0} commands in one transaction. {memoryStream.Length.BytesToString()} bytes.");
                    }


                    //wait for it to be deleted by counterpart, signaling it was processed
                    Extensions.Time(
                        _ => { },
                        () =>
                        {
                            try
                            {
                                var result = !fileAccess.Exists(WriteToFilename);
                                return result;
                            }
                            catch (Exception ex)
                            {
                                if (Verbose)
                                {
                                    Program.Log($"[{writeFileShortName}] [Check file has been processed]: {ex.Message}");
                                }
                                return false;
                            }
                        },
                        _ => 1,
                        $"[{writeFileShortName}] Waiting for file to be deleted",
                        Verbose);




                    //move the file into place
                    var moved = 0;
                    Extensions.Time(
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
                                        Program.Log($"[{writeFileShortName}] [Rewrite attempt {attempt:N0}]: {ex.Message}");
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                if (Verbose)
                                {
                                    Program.Log($"[{writeFileShortName}] [Move file into place - attempt {attempt:N0}]: {ex.Message}");
                                }
                            }
                        },
                        () => moved == 1,
                        attempt =>
                        {
                            //rclone ftp mount throws "The request could not be performed because of an I/O device error" if file move requests are done in quick succession
                            return 20;
                        },
                        $"[{writeFileShortName}] Waiting for file to be moved into place",
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
                        _ => { },
                        () =>
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
                                    Program.Log($"[{ReadFromFilename}] [Checking if file exists]: {ex.Message}");
                                }
                                return false;
                            }
                        },
                        _ => 1,
                        $"[{readFileShortName}] Waiting for file to exist",
                        Verbose);




                    byte[] fileContent = [];
                    Extensions.Time(
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
                                    Program.Log($"[{readFileShortName}] [Reading file contents - attempt {attempt:N0}]: {ex.Message}");
                                }
                            }
                        },
                        () => fileContent?.Length > 0,
                        _ => 1,
                        $"[{readFileShortName}] Reading file contents",
                        Verbose);



                    fileAccess.Delete(ReadFromFilename);



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
                        Program.Log($"[{readFileShortName}] Read {commandsRead:N0} commands in one transaction. {memoryStream.Length.BytesToString()} bytes.");
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

        public override void Stop(string reason)
        {
            Program.Log($"{nameof(WriteThenWaitForDelete)}: Stopping. Reason: {reason}");
        }
    }
}
