using ft.Commands;
using ft.IO.Files;
using ft.Streams;
using ft.Utilities;
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
        private readonly int paceMilliseconds;
        private readonly PacedAccess fileAccess;

        public UploadDownload(
                    IFileAccess fileAccess,
                    string readFromFilename,
                    string writeToFilename,
                    int maxFileSizeBytes,
                    int tunnelTimeoutMilliseconds,
                    int paceMilliseconds,
                    bool verbose) : base(readFromFilename, writeToFilename, tunnelTimeoutMilliseconds, verbose)
        {
            this.fileAccess = new PacedAccess(fileAccess, paceMilliseconds);
            this.maxFileSizeBytes = maxFileSizeBytes;
            this.paceMilliseconds = Math.Max(1, paceMilliseconds);  //the pace should be at least 1 millisecond, otherwise we consume a lot of CPU cycles
        }

        public override void SendPump()
        {
            var writeFileShortName = Path.GetFileName(WriteToFilename);
            var writingStopwatch = new Stopwatch();

            var timeSinceWrite = new Stopwatch();
            byte[]? lastWrite = null;

            try
            {
                fileAccess.Delete(WriteToFilename);
            }
            catch { }

            //var debugFilename = $"diag-sent-{Environment.MachineName}.txt";
            //File.Create(debugFilename).Close();

            while (true)
            {
                try
                {
                    using var memoryStream = new MemoryStream();
                    var hashingStream = new HashingStream(memoryStream, Verbose, TunnelTimeoutMilliseconds);
                    var binaryWriter = new BinaryWriter(hashingStream);

                    if (SendQueue.TryTake(out Command? command, TunnelTimeoutMilliseconds))
                    {
                        command.Serialise(binaryWriter);
                    }

                    binaryWriter.Flush(true, Verbose, TunnelTimeoutMilliseconds);

                    var commandBytes = memoryStream.ToArray();

                    Extensions.Time(
                        $"[{writeFileShortName}] Write file",
                        _ =>
                        {
                            var writeSuccessful = false;

                            try
                            {
                                if (timeSinceWrite.ElapsedMilliseconds < TunnelTimeoutMilliseconds * 0.4)
                                {
                                    fileAccess.WriteAllBytes(WriteToFilename, commandBytes, false);     //only write the file when the existing one has been deleted (signifying it was processed by the counterpart)

                                    //File.AppendAllLines(debugFilename, [$"{commandBytes.Length:N0} bytes", Convert.ToBase64String(memoryStream.ToArray())]);

                                    writeSuccessful = true;
                                }
                                else
                                {
                                    //SMB Windows-Linux-Windows occassionally writes a 0 byte file. Let's re-write it.

                                    if (lastWrite != null)
                                    {
                                        Program.Log($"[{writeFileShortName}] File still exists after {timeSinceWrite.ElapsedMilliseconds:N0} ms. Re-writing.");

                                        fileAccess.WriteAllBytes(WriteToFilename, lastWrite);

                                        //File.AppendAllLines(debugFilename, [$"{lastWrite.Length:N0} bytes re-write", Convert.ToBase64String(lastWrite)]);

                                        timeSinceWrite.Restart();
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                if (Verbose)
                                {
                                    Program.Log($"[{writeFileShortName}] Error during write: {ex.Message}");
                                }
                            }



                            return writeSuccessful;
                        },
                        DefaultSleepStrategy,
                        Verbose);

                    if (command != null)
                    {
                        CommandSent(command);
                        lastWrite = commandBytes;
                        timeSinceWrite.Restart();

                        if (Verbose)
                        {
                            Program.Log($"[{writeFileShortName}] Wrote {memoryStream.Length.BytesToString()}.");
                        }
                    }

                    Delay.Wait(paceMilliseconds);
                }
                catch (Exception ex)
                {
                    Program.Log($"[{writeFileShortName}] {nameof(SendPump)}: {ex.Message}");
                    Program.Log($"[{writeFileShortName}] Restarting {nameof(SendPump)}");

                    Delay.Wait(1000);
                }
            }
        }

        public override void ReceivePump()
        {
            //var debugFilename = $"diag-received-{Environment.MachineName}.txt";
            //File.Create(debugFilename).Close();

            var readFileShortName = Path.GetFileName(ReadFromFilename);

            ulong? previousPacketNumber = null;
            uint previousPacketCRC = 0;

            while (true)
            {
                try
                {
                    byte[] fileContent = [];

                    Extensions.Time(
                        $"[{readFileShortName}] Read file",
                        _ =>
                        {
                            var readSuccessful = false;

                            try
                            {
                                fileContent = fileAccess.ReadAllBytes(ReadFromFilename);

                                if (Verbose)
                                {
                                    Program.Log($"[{readFileShortName}] Read {fileContent.Length.BytesToString()}.");
                                }

                                if (fileContent == null ||fileContent.Length == 0)
                                {
                                    Program.Log($"[{readFileShortName}] 0 length read. Retrying.");
                                }
                                else
                                {
                                    readSuccessful = true;
                                }
                            }
                            catch (Exception ex)
                            {
                                if (Verbose)
                                {
                                Program.Log($"[{readFileShortName}] Could not read: {ex.Message}");
                            }
                            }

                            return readSuccessful;
                        },
                        DefaultSleepStrategy,
                        Verbose);

                    Delay.Wait(paceMilliseconds);

                    Command? command = null;

                    if (fileContent.Length > 0)
                    {
                        using var memoryStream = new MemoryStream(fileContent);
                        var hashingStream = new HashingStream(memoryStream, Verbose, TunnelTimeoutMilliseconds);
                        var binaryReader = new BinaryReader(hashingStream, Encoding.ASCII);

                        if (Verbose)
                        {
                            Program.Log($"[{readFileShortName}] Processing file content");
                        }

                        try
                        {
                            command = Command.Deserialise(binaryReader);
                        }
                        catch (InvalidDataException)
                        {
                            Program.Log($"[{readFileShortName}] Malformed packet received. Ignoring and awaiting resend.");
                            continue;
                        }
                        catch (EndOfStreamException eosEx)
                        {
                            Program.Log($"[{readFileShortName}] {eosEx.Message}");
                        }

                        if (command == null)
                        {
                            var exMsg = $"[{readFileShortName}] Could not read command.";
                            if (Verbose)
                            {
                                Program.Log(exMsg);
                            }
                            throw new Exception(exMsg);
                        }

                        if (command.PacketNumber == previousPacketNumber && command.CRC == previousPacketCRC)
                        {
                            //File.AppendAllLines(debugFilename, [$"{fileContent.Length:N0} bytes (discarding duplicate)", Convert.ToBase64String(fileContent)]);
                            Program.Log($"[{readFileShortName}] Discarding duplicate packet (Packet number: {command.PacketNumber}, Size: {fileContent.Length:N0} bytes, CRC: {command.CRC})", ConsoleColor.Yellow);
                        }
                        else
                        {
                            //File.AppendAllLines(debugFilename, [$"{fileContent.Length:N0} bytes", Convert.ToBase64String(fileContent)]);

                            previousPacketNumber = command.PacketNumber;
                            previousPacketCRC = command.CRC;

                            if (Verbose)
                            {
                                Program.Log($"[{readFileShortName}] Received packet number {command.PacketNumber} ({command.GetName()})");
                            }

                            CommandReceived(command);
                        }
                    }

                    Extensions.Time(
                        $"[{readFileShortName}] Move processed file",
                        _ =>
                        {
                            var moveSuccessful = false;

                            try
                            {
                                //Moving seems to block the file system much less than deleting
                                fileAccess.Move(ReadFromFilename, ReadFromFilename + ".processed", true);
                                moveSuccessful = true;
                            }
                            catch (Exception ex)
                            {
                                Program.Log($"[{readFileShortName}] Could not move: {ex.Message}");
                            }

                            return moveSuccessful;
                        },
                        DefaultSleepStrategy,
                        Verbose);

                    if (Verbose)
                    {
                        Program.Log($"[{readFileShortName}] Read {fileContent.Length.BytesToString()}.");
                        Program.Log($"[{readFileShortName}] Finished processing file content");
                    }
                }
                catch (Exception ex)
                {
                    Program.Log($"[{readFileShortName}] {nameof(ReceivePump)}: {ex}");
                    Program.Log($"[{readFileShortName}] Restarting {nameof(ReceivePump)}");

                    Delay.Wait(1000);
                }
            }
        }

        int DefaultSleepStrategy((int Attempt, TimeSpan Elapsed, string Operation) attempt)
        {
            if (attempt.Elapsed.TotalMilliseconds > TunnelTimeoutMilliseconds)
            {
                throw new Exception($"{attempt.Operation} has exceeded the tunnel timeout of {TunnelTimeoutMilliseconds:N0} ms. Cancelling.");
            }

            //Tuned for SMB Windows-Windows-Windows
            //if (attempt.Elapsed.TotalMilliseconds < 100) return 1;
            //if (attempt.Elapsed.TotalMilliseconds < 1000) return 20;
            //return 100;

            return paceMilliseconds;
        }

        public override void Stop(string reason)
        {
            Program.Log($"{nameof(UploadDownload)}: Stopping. Reason: {reason}");
        }
    }
}
