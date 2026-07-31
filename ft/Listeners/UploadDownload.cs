using ft.CLI;
using ft.Commands;
using ft.IO.Files;
using ft.Streams;
using ft.Utilities;
using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.RateLimiting;
using System.Threading.Tasks;

namespace ft.Listeners;

public class UploadDownload : SharedFileManager
{
    private readonly PacedAccess fileAccess;

    public UploadDownload(
        IFileAccess fileAccess,
        string readFromFilename,
        string writeToFilename,
        int tunnelTimeoutMilliseconds,
        int maxSubfiles,
        long maxFileSizeBytes,
        bool blockingReader,
        bool verbose) : base(readFromFilename, writeToFilename, tunnelTimeoutMilliseconds, verbose)
    {
        Options.PaceMilliseconds =
            Math.Max(1,
                Options.PaceMilliseconds); //the pace should be at least 1 millisecond, otherwise we consume a lot of CPU cycles

        this.fileAccess = new PacedAccess(fileAccess, Options.PaceMilliseconds);
        this.maxSubfiles = maxSubfiles;
        this.maxFileSizeBytes = maxFileSizeBytes;
        this.blockingReader = blockingReader;

        //this class can combine multiple commands into a single file
        SendQueue = new BlockingCollection<Command>(20);

        if (Options.WriteIntervalMilliseconds > 0)
        {
            writeLimiter = new FixedWindowRateLimiter(
                new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 1,
                    Window = TimeSpan.FromMilliseconds(Options.WriteIntervalMilliseconds),
                    QueueLimit = int.MaxValue,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst
                });
        }

        if (Options.ReadIntervalMilliseconds > 0)
        {
            readLimiter = new FixedWindowRateLimiter(
                new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 1,
                    Window = TimeSpan.FromMilliseconds(Options.ReadIntervalMilliseconds),
                    QueueLimit = int.MaxValue,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst
                });
        }
    }

    private readonly ReplenishingRateLimiter? writeLimiter;
    private readonly ReplenishingRateLimiter? readLimiter;

    private static string GetSubfileName(string filename, int index, int maxSubfiles)
    {
        string result;
        if (maxSubfiles == 1)
        {
            result = filename;
        }
        else
        {
            var originalExtension = Path.GetExtension(filename);
            result = Path.ChangeExtension(filename, $"ft{index}{originalExtension}");
        }

        return result;
    }

    private readonly int maxSubfiles;
    private readonly long maxFileSizeBytes;
    private readonly bool blockingReader;
    private readonly ConcurrentDictionary<string, DateTime> filesInUse = [];

    public override async Task SendPumpAsync()
    {
        var writeFileShortName = Path.GetFileName(WriteToFilename);

        for (int i = 1; i <= maxSubfiles; i++)
        {
            try
            {
                var subFilename = GetSubfileName(WriteToFilename, i, maxSubfiles);
                await fileAccess.DeleteAsync(subFilename);
            }
            catch
            {
                // ignored
            }
        }

        var sessionId = Random.Shared.NextInt64();
        var fileIx = 1;

        while (true)
        {
            try
            {
                var writeToFilename = GetSubfileName(WriteToFilename, fileIx, maxSubfiles);

                await Extensions.TimeAsync(
                    $"[{writeFileShortName}] Wait for file to be available",
                    async attempt =>
                    {
                        bool fileIsAvailable;

                        if (filesInUse.TryGetValue(writeToFilename, out var sentDate))
                        {
                            var timeSinceSent = DateTime.Now - sentDate;
                            if (timeSinceSent.TotalMilliseconds < TunnelTimeoutMilliseconds)
                            {
                                //the file has not been acknowledged yet
                                fileIsAvailable = false;
                            }
                            else
                            {
                                //the file was never acknowledged
                                fileIsAvailable = true;
                            }
                        }
                        else
                        {
                            //the file is not currently in use
                            fileIsAvailable = true;
                        }

                        if (attempt.Elapsed.TotalMilliseconds > 0.5 * TunnelTimeoutMilliseconds)
                        {
                            fileIsAvailable = !await fileAccess.ExistsAsync(writeToFilename);
                            if (fileIsAvailable)
                            {
                                Program.Log(
                                    $"[{writeFileShortName}] Confirmed file is no longer present: {writeToFilename}.");
                            }
                        }

                        return fileIsAvailable;
                    },
                    DefaultSleepStrategy,
                    Verbose);


                //Wait without touching the write file, which lets rclone sync.
                //By waiting here, we allow commands to accumulate which lets us write them to a single further below.
                using var limit = await writeLimiter.AcquireAsync();

                using var memoryStream = new MemoryStream();
                var hashingStream = new HashingStream(memoryStream, Verbose, TunnelTimeoutMilliseconds);
                var binaryWriter = new BinaryWriter(hashingStream);

                var commandsSent = 0;
                int? commandsToSend = null;
                while (true)
                {
                    hashingStream.Reset();

                    if (SendQueue.TryTake(out var command, TunnelTimeoutMilliseconds))
                    {
                        AssignSendSequence(command);

                        if (commandsSent == 0)
                        {
                            //write the file header

                            binaryWriter.Write(sessionId);

                            var currentEpochDate = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                            binaryWriter.Write(currentEpochDate);
                        }

                        commandsToSend ??= SendQueue.Count + 1;
                        command.Serialise(binaryWriter);
                    }

                    binaryWriter.Flush(Verbose, TunnelTimeoutMilliseconds);

                    if (command != null)
                    {
                        CommandSent(command);
                        commandsSent++;

                        if (Verbose)
                        {
                            Program.Log(
                                $"[{writeFileShortName}] Sent packet number {command.PacketNumber} ({command.GetName()})");
                        }
                    }

                    if (commandsToSend.HasValue && commandsSent >= commandsToSend.Value)
                    {
                        break;
                    }

                    //Cap the file size. With a small cap (e.g. 64 KB on 9p) each file reads back in
                    //~one round-trip, well under the tunnel timeout, so a slow reader never ages a
                    //subfile out and the writer never has to overwrite an unread one.
                    if (maxFileSizeBytes > 0 && memoryStream.Length >= maxFileSizeBytes)
                    {
                        break;
                    }
                }

                if (Verbose)
                {
                    Program.Log(
                        $"[{writeFileShortName}] Serialised {commandsSent:N0} commands into {Path.GetFileName(writeToFilename)} ({memoryStream.Length.BytesToString()})");
                }

                memoryStream.TryGetBuffer(out var buffer);
                var commandBytes = buffer.AsMemory();

                await Extensions.TimeAsync(
                    $"[{writeFileShortName}] Write file",
                    async _ =>
                    {
                        var writeSuccessful = false;

                        try
                        {
                            await fileAccess.DeleteAsync(writeToFilename);
                        }
                        catch
                        {
                            // ignored
                        }

                        try
                        {
                            await fileAccess.WriteAllBytesAsync(writeToFilename, commandBytes, true);

                            filesInUse[writeToFilename] = DateTime.Now;

                            if (maxSubfiles > 1)
                            {
                                //the main file contains metadata about the session
                                var sessionIdBuffer = new byte[sizeof(long)];
                                BinaryPrimitives.WriteInt64LittleEndian(sessionIdBuffer, sessionId);

                                await fileAccess.WriteAllBytesAsync(WriteToFilename, sessionIdBuffer, true);
                            }

                            writeSuccessful = true;
                            fileIx++;

                            if (fileIx > maxSubfiles)
                            {
                                fileIx = 1;
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


                Delay.Wait(Options.PaceMilliseconds);
            }
            catch (Exception ex)
            {
                Program.Log($"[{writeFileShortName}] {nameof(SendPumpAsync)}: {ex.Message}");
                Program.Log($"[{writeFileShortName}] Restarting {nameof(SendPumpAsync)}");

                Delay.Wait(1000);
            }
        }
    }

    private static async Task<long> ReadSessionMetadataAsync(IFileAccess fileAccess, string filename)
    {
        await using var sessionMetadataBytes = await fileAccess.GetStreamAsync(filename);
        var buffer = new byte[sizeof(long)];
        await sessionMetadataBytes.ReadExactlyAsync(buffer);
        return BinaryPrimitives.ReadInt64LittleEndian(buffer);
    }

    public override async Task ReceivePumpAsync()
    {
        //var debugFilename = $"diag-received-{Environment.MachineName}.txt";
        //File.Create(debugFilename).Close();

        var readFileShortName = Path.GetFileName(ReadFromFilename);

        //a file can contain multiple commands, so remember recently processed packets to avoid re-delivering any of them when a file is re-read
        const int MAX_RECENT_PACKETS = 1000;
        var recentPackets = new HashSet<(ulong PacketNumber, uint CRC)>();
        var recentPacketsOrder = new Queue<(ulong PacketNumber, uint CRC)>();

        long? currentSessionId = null;
        int? readFromIx = null;
        var sessionCheckStopwatch = Stopwatch.StartNew();

        while (true)
        {
            try
            {
                if (readFromIx == null)
                {
                    try
                    {
                        currentSessionId = await ReadSessionMetadataAsync(fileAccess, ReadFromFilename);

                        if (Verbose)
                        {
                            Program.Log(
                                $"[{readFileShortName}] Read session metadata. [{nameof(currentSessionId)} = {currentSessionId}]");
                        }

                        if (maxSubfiles == 1)
                        {
                            readFromIx = 1;
                        }
                        else
                        {
                            readFromIx = currentSessionId.HasValue
                                ? await GetFileIndexBySessionIdAsync(currentSessionId.Value)
                                : 1;

                            var candidateFilename = GetSubfileName(ReadFromFilename, readFromIx.Value, maxSubfiles);

                            if (Verbose)
                            {
                                Program.Log(
                                    $"[{readFileShortName}] The latest file from counterpart appears to be: {Path.GetFileName(candidateFilename)}");
                            }
                        }
                    }
                    catch
                    {
                        if (Verbose)
                        {
                            Program.Log(
                                $"[{readFileShortName}] Could not determine the current index from {ReadFromFilename}");
                        }

                        Delay.Wait(1000);
                        continue;
                    }
                }

                using var limit = await readLimiter.AcquireAsync();

                var readFromFilename = GetSubfileName(ReadFromFilename, readFromIx.Value, maxSubfiles);

                Stream? fileContent = null;

                if (blockingReader)
                {
                    //BLOCKING read (FTP, and any transport whose access layer shares one connection): retry
                    //the current slot until it appears, regardless of subfile count. FTP serializes every op
                    //- reads, writes, and the keep-alive pings - through a single connection, and one hung
                    //~4s data-connection op freezes all of it. The reader idling here (instead of polling) is
                    //what leaves that connection free for pings, keeping FTP online - its proven behaviour at
                    //ANY subfile count. The non-blocking poll below would hammer the connection and starve
                    //the pings into a false offline. Gated on the transport, NOT maxSubfiles.
                    var checkForSessionChange = Stopwatch.StartNew();

                    await Extensions.TimeAsync(
                        $"[{readFileShortName}] Read file",
                        async _ =>
                        {
                            var readSuccessful = false;

                            try
                            {
                                fileContent = await fileAccess.GetStreamAsync(readFromFilename);
                            }
                            catch (Exception ex)
                            {
                                if (Verbose)
                                {
                                    Program.Log($"[{readFileShortName}] Could not read: {ex.Message}");
                                }
                            }

                            if (checkForSessionChange.ElapsedMilliseconds > 5000)
                            {
                                long? latestSessionId = null;
                                try
                                {
                                    latestSessionId = await ReadSessionMetadataAsync(fileAccess, ReadFromFilename);
                                }
                                catch
                                {
                                    // ignored
                                }

                                if (latestSessionId.HasValue && latestSessionId != currentSessionId)
                                {
                                    if (Verbose)
                                    {
                                        Program.Log($"[{readFileShortName}] New session detected: {latestSessionId}");
                                    }

                                    currentSessionId = latestSessionId;
                                    readFromIx = 1;
                                    readFromFilename = GetSubfileName(ReadFromFilename, readFromIx.Value, maxSubfiles);
                                }

                                checkForSessionChange.Restart();
                            }

                            return readSuccessful;
                        },
                        DefaultSleepStrategy,
                        Verbose);
                }
                else
                {
                    //Multi-subfile transports (9p): NON-blocking. Periodic session-change check, decoupled
                    //from the read so it still runs when this slot is absent.
                    if (sessionCheckStopwatch.ElapsedMilliseconds > 5000)
                    {
                        try
                        {
                            var latestSessionId = await ReadSessionMetadataAsync(fileAccess, ReadFromFilename);
                            if (latestSessionId != currentSessionId)
                            {
                                if (Verbose)
                                {
                                    Program.Log($"[{readFileShortName}] New session detected: {latestSessionId}");
                                }

                                currentSessionId = latestSessionId;
                                readFromIx = 1;
                                readFromFilename = GetSubfileName(ReadFromFilename, readFromIx.Value, maxSubfiles);
                            }
                        }
                        catch
                        {
                            // ignored
                        }

                        sessionCheckStopwatch.Restart();
                    }

                    //Read this slot ONCE - do NOT block/retry on a missing slot. Fixating on one slot made
                    //the reader deaf to the OTHER slots (and the pings carried in them), which stalled the
                    //tunnel into a false offline. If the slot isn't here yet we just skip it; the reorder
                    //buffer reassembles whatever order the present slots arrive in, and we pick this one up
                    //on a later pass once the writer has produced it.
                    try
                    {
                        fileContent = await fileAccess.GetStreamAsync(readFromFilename);

                        if (Verbose && fileContent?.Length > 0)
                        {
                            Program.Log(
                                $"[{readFileShortName}] Read {fileContent.Length.BytesToString()} from {Path.GetFileName(readFromFilename)}.");
                        }
                    }
                    catch
                    {
                        //slot not present yet - skip it this pass (no blocking, no retry)
                    }
                }

                await using (fileContent)
                {
                    await Task.Delay(Options.PaceMilliseconds);

                    readFromIx++;
                    if (readFromIx > maxSubfiles)
                    {
                        readFromIx = 1;
                    }

                    Command? command = null;

                    if (fileContent is not null)
                    {
                        var hashingStream = new HashingStream(fileContent, Verbose, TunnelTimeoutMilliseconds);
                        var binaryReader = new BinaryReader(hashingStream, Encoding.ASCII);

                        var filesSessionId = binaryReader.ReadInt64();
                        var dateWritten = binaryReader.ReadInt64();

                        var commandsProcessed = 0;


                        while (true)
                        {
                            hashingStream.Reset();

                            if (Verbose)
                            {
                                Program.Log($"[{readFileShortName}] Processing file content");
                            }

                            try
                            {
                                command = Command.Deserialise(binaryReader);
                                commandsProcessed++;
                            }
                            catch (InvalidDataException)
                            {
                                Program.Log(
                                    $"[{readFileShortName}] Malformed packet received. Ignoring and awaiting resend.");
                                continue;
                            }
                            catch (EndOfStreamException eosEx)
                            {
                                Program.Log($"[{readFileShortName}] {eosEx.Message}");
                                break;
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

                            if (!recentPackets.Add((command.PacketNumber, command.CRC)))
                            {
                                Program.Log(
                                    $"[{readFileShortName}] Discarding duplicate packet (Packet number: {command.PacketNumber}, Size: {fileContent.Length:N0} bytes, CRC: {command.CRC})",
                                    ConsoleColor.Yellow);
                            }
                            else
                            {
                                recentPacketsOrder.Enqueue((command.PacketNumber, command.CRC));
                                while (recentPacketsOrder.Count > MAX_RECENT_PACKETS)
                                {
                                    recentPackets.Remove(recentPacketsOrder.Dequeue());
                                }

                                if (Verbose)
                                {
                                    Program.Log(
                                        $"[{readFileShortName}] Received packet number {command.PacketNumber} ({command.GetName()})");
                                }

                                CommandReceived(command);
                            }
                        }

                        if (Verbose)
                        {
                            Program.Log(
                                $"[{readFileShortName}] Deserialized {commandsProcessed:N0} commands from one file ({fileContent.Length.BytesToString()})");
                        }

                        var filesInUseSnapshot = filesInUse.ToList();
                        foreach (var entry in filesInUseSnapshot)
                        {
                            if (await fileAccess.ExistsAsync(entry.Key))
                            {
                                var timeSinceSent = DateTime.Now - entry.Value;
                                if (timeSinceSent.TotalMilliseconds > TunnelTimeoutMilliseconds)
                                {
                                    try
                                    {
                                        await fileAccess.DeleteAsync(entry.Key);
                                    }
                                    catch
                                    {
                                        // ignored
                                    }

                                    filesInUse.TryRemove(entry.Key, out var _);
                                }
                            }
                            else
                            {
                                filesInUse.TryRemove(entry.Key, out var _);
                            }
                        }

                        if (Verbose)
                        {
                            Program.Log($"[{readFileShortName}] There are currently {filesInUse.Count} files in use");
                        }
                    }

                    if (blockingReader)
                    {
                        //Blocking-reader path (FTP): retry the delete until it succeeds - the original
                        //behaviour. On FTP's single serialized connection a transient delete that ISN'T
                        //retried leaves the subfile in place, and the writer then blocks waiting to reuse that
                        //slot, stalling the transfer. fileContent is always present here (the read above blocks
                        //until it is), so there's always exactly one slot to delete.
                        await Extensions.TimeAsync(
                            $"[{readFileShortName}] Delete processed file",
                            async _ =>
                            {
                                var deleteSuccessful = false;

                                try
                                {
                                    await fileAccess.DeleteAsync(readFromFilename);
                                    deleteSuccessful = true;
                                }
                                catch (Exception ex)
                                {
                                    if (Verbose)
                                    {
                                        Program.Log(
                                            $"[{readFileShortName}] Could not delete {Path.GetFileName(readFromFilename)}: {ex.Message}");
                                    }
                                }

                                return deleteSuccessful;
                            },
                            DefaultSleepStrategy,
                            Verbose);
                    }
                    else if (fileContent is not null)
                    {
                        //TODO: check read
                        //Non-blocking-reader path (9p): delete only a slot we actually read+processed.
                        //fileContent is empty when the slot was absent and skipped - nothing to delete, and
                        //deleting an absent slot would just race the writer creating it.
                        try
                        {
                            await fileAccess.DeleteAsync(readFromFilename);
                        }
                        catch (Exception ex)
                        {
                            if (Verbose)
                            {
                                Program.Log(
                                    $"[{readFileShortName}] Could not delete {Path.GetFileName(readFromFilename)}: {ex.Message}");
                            }
                        }
                    }

                    if (Verbose)
                    {
                        Program.Log($"[{readFileShortName}] Read {fileContent?.Length.BytesToString()}.");
                        Program.Log($"[{readFileShortName}] Finished processing file content");
                    }
                }
            }
            catch (Exception ex)
            {
                Program.Log($"[{readFileShortName}] {nameof(ReceivePumpAsync)}: {ex.Message}");
                Program.Log($"[{readFileShortName}] Restarting {nameof(ReceivePumpAsync)}");

                readFromIx = null;

                Delay.Wait(1000);
            }
        }
    }

    private async Task<int> GetFileIndexBySessionIdAsync(long sessionId)
    {
        var maxDate = 0L;
        var index = 1;

        for (int i = 0; i < maxSubfiles; i++)
        {
            var fileName = GetSubfileName(ReadFromFilename, i, maxSubfiles);
            try
            {
                if (!await fileAccess.ExistsAsync(fileName))
                {
                    continue;
                }

                await using var stream = await fileAccess.GetStreamAsync(fileName);
                using var reader = new BinaryReader(stream);
                var fileSessionId = reader.ReadInt64();

                if (fileSessionId != sessionId)
                {
                    continue;
                }

                var date = reader.ReadInt64();

                if (date <= maxDate) continue;

                maxDate = date;
                index = i;
            }
            catch
            {
                // Ignore
            }
        }

        return index;
    }

    private int DefaultSleepStrategy((int Attempt, TimeSpan Elapsed, string Operation) attempt)
    {
        if (attempt.Elapsed.TotalMilliseconds > TunnelTimeoutMilliseconds)
        {
            throw new Exception(
                $"{attempt.Operation} has exceeded the tunnel timeout of {TunnelTimeoutMilliseconds:N0} ms. Cancelling.");
        }

        //Tuned for SMB Windows-Windows-Windows
        //if (attempt.Elapsed.TotalMilliseconds < 100) return 1;
        //if (attempt.Elapsed.TotalMilliseconds < 1000) return 20;
        //return 100;

        var toSleep = Options.PaceMilliseconds;

        return toSleep;
    }

    public override void Stop(string reason)
    {
        Program.Log($"{nameof(UploadDownload)}: Stopping. Reason: {reason}");
    }
}