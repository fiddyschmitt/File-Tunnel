using ft.Commands;
using ft.IO.Sqs;
using ft.Streams;
using ft.Utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.RateLimiting;
using System.Threading.Tasks;

namespace ft.Listeners
{
    public class SqsTunnel : SharedFileManager
    {
        private readonly SqsClient client;
        private readonly int maxMessageSize;
        private readonly ReplenishingRateLimiter? WriteLimiter;
        private readonly SemaphoreSlim sendSemaphore;

        public SqsTunnel(
            string region,
            string accessKey,
            string secretKey,
            string readQueueUrl,
            string writeQueueUrl,
            int tunnelTimeoutMilliseconds,
            bool verbose,
            int maxConnections,
            int maxMessageSize)
            : base(readQueueUrl, writeQueueUrl, tunnelTimeoutMilliseconds, verbose)
        {
            this.client = new SqsClient(region, accessKey, secretKey, maxConnections);
            this.maxMessageSize = maxMessageSize;

            this.sendSemaphore = new SemaphoreSlim(maxConnections, maxConnections);

            SendQueue = new System.Collections.Concurrent.BlockingCollection<Command>(20);

            if (CLI.Options.WriteIntervalMilliseconds > 0)
            {
                WriteLimiter = new FixedWindowRateLimiter(
                    new FixedWindowRateLimiterOptions()
                    {
                        PermitLimit = 1,
                        Window = TimeSpan.FromMilliseconds(CLI.Options.WriteIntervalMilliseconds),
                        QueueLimit = int.MaxValue,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst
                    });
            }
        }

        public override void Start()
        {
            try
            {
                Program.Log($"[SQS] Draining old messages from receive queue...");
                while (true)
                {
                    var msgs = client.ReceiveMessages(ReadFromFilename, maxMessages: 10, waitTimeSeconds: 1);
                    if (msgs.Count == 0) break;

                    foreach (var msg in msgs)
                    {
                        client.DeleteMessage(ReadFromFilename, msg.ReceiptHandle);
                    }
                }
            }
            catch (Exception ex)
            {
                Program.Log($"[SQS] Drain warning: {ex.Message}", ConsoleColor.Yellow);
            }

            base.Start();
        }

        public override void SendPump()
        {
            var queueName = new Uri(WriteToFilename).Segments[^1];

            while (true)
            {
                try
                {
                    WriteLimiter?.Wait();

                    using var memoryStream = new MemoryStream();
                    var hashingStream = new HashingStream(memoryStream, Verbose, TunnelTimeoutMilliseconds);
                    var binaryWriter = new BinaryWriter(hashingStream);

                    while (true)
                    {
                        if (memoryStream.Length > 0 && SendQueue.Count == 0)
                        {
                            break;
                        }

                        int timeout = memoryStream.Length == 0 ? TunnelTimeoutMilliseconds : 0;

                        hashingStream.Reset();
                        if (SendQueue.TryTake(out Command? command, timeout))
                        {
                            AssignSendSequence(command);
                            command.Serialise(binaryWriter);
                            binaryWriter.Flush(Verbose, TunnelTimeoutMilliseconds);
                            CommandSent(command);

                            if (Verbose)
                            {
                                Program.Log($"[SQS:{queueName}] Packaged packet number {command.PacketNumber} ({command.GetName()})");
                            }

                            if (maxMessageSize > 0 && memoryStream.Length >= maxMessageSize)
                            {
                                break;
                            }
                        }
                        else
                        {
                            break;
                        }
                    }

                    if (memoryStream.Length > 0)
                    {
                        var base64Data = Convert.ToBase64String(memoryStream.ToArray());

                        sendSemaphore.Wait();
                        Task.Run(() =>
                        {
                            try
                            {
                                Extensions.Time(
                                    $"[SQS:{queueName}] Write message",
                                    _ =>
                                    {
                                        try
                                        {
                                            client.SendMessage(WriteToFilename, base64Data);
                                            return true;
                                        }
                                        catch (Exception ex)
                                        {
                                            Program.Log($"[SQS:{queueName}] Write error: {ex.Message}", ConsoleColor.Yellow);
                                            return false;
                                        }
                                    },
                                    attempt =>
                                    {
                                        if (attempt.Elapsed.TotalMilliseconds > TunnelTimeoutMilliseconds)
                                        {
                                            throw new Exception($"SQS SendMessage exceeded tunnel timeout of {TunnelTimeoutMilliseconds} ms.");
                                        }
                                        return 500;
                                    },
                                    Verbose);
                            }
                            finally
                            {
                                sendSemaphore.Release();
                            }
                        });
                    }
                }
                catch (Exception ex)
                {
                    Program.Log($"[SQS:{queueName}] {nameof(SendPump)}: {ex.Message}");
                    Delay.Wait(1000);
                }
            }
        }

        public override void ReceivePump()
        {
            var queueName = new Uri(ReadFromFilename).Segments[^1];
            var recentPackets = new HashSet<(ulong PacketNumber, uint CRC)>();
            var recentPacketsOrder = new Queue<(ulong PacketNumber, uint CRC)>();
            const int MAX_RECENT_PACKETS = 1000;

            while (true)
            {
                try
                {
                    List<SqsMessage> messages = [];

                    Extensions.Time(
                        $"[SQS:{queueName}] Receive message",
                        _ =>
                        {
                            try
                            {
                                messages = client.ReceiveMessages(ReadFromFilename, maxMessages: 10, waitTimeSeconds: 0);
                                return true;
                            }
                            catch (Exception ex)
                            {
                                Program.Log($"[SQS:{queueName}] Read error: {ex.Message}", ConsoleColor.Yellow);
                                return false;
                            }
                        },
                        attempt =>
                        {
                            if (attempt.Elapsed.TotalMilliseconds > TunnelTimeoutMilliseconds * 2)
                            {
                                throw new Exception($"SQS ReceiveMessage exceeded tunnel timeout bound.");
                            }
                            return 1000;
                        },
                        Verbose);

                    foreach (var msg in messages)
                    {
                        if (string.IsNullOrEmpty(msg.Body)) continue;

                        byte[] fileContent = Convert.FromBase64String(msg.Body);

                        using var memoryStream = new MemoryStream(fileContent);
                        var hashingStream = new HashingStream(memoryStream, Verbose, TunnelTimeoutMilliseconds);
                        var binaryReader = new BinaryReader(hashingStream, Encoding.ASCII);

                        while (memoryStream.Position < memoryStream.Length)
                        {
                            hashingStream.Reset();
                            Command? command = null;

                            try
                            {
                                command = Command.Deserialise(binaryReader);
                            }
                            catch (InvalidDataException)
                            {
                                continue;
                            }
                            catch (EndOfStreamException)
                            {
                                break;
                            }

                            if (command == null) continue;

                            if (!recentPackets.Add((command.PacketNumber, command.CRC)))
                            {
                                if (Verbose)
                                {
                                    Program.Log($"[SQS:{queueName}] Discarding duplicate packet (Packet number: {command.PacketNumber})", ConsoleColor.Yellow);
                                }
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
                                    Program.Log($"[SQS:{queueName}] Received packet number {command.PacketNumber} ({command.GetName()})");
                                }

                                CommandReceived(command);
                            }
                        }

                        var receiptHandle = msg.ReceiptHandle;
                        Task.Run(() =>
                        {
                            Extensions.Time(
                                $"[SQS:{queueName}] Delete message",
                                _ =>
                                {
                                    try
                                    {
                                        client.DeleteMessage(ReadFromFilename, receiptHandle);
                                        return true;
                                    }
                                    catch (Exception ex)
                                    {
                                        Program.Log($"[SQS:{queueName}] Delete error: {ex.Message}", ConsoleColor.Yellow);
                                        return false;
                                    }
                                },
                                attempt =>
                                {
                                    if (attempt.Elapsed.TotalMilliseconds > TunnelTimeoutMilliseconds)
                                    {
                                        throw new Exception("SQS DeleteMessage exceeded tunnel timeout bound.");
                                    }
                                    return 500;
                                },
                                Verbose);
                        });
                    }

                    if (messages.Count == 0)
                    {
                        Delay.Wait(CLI.Options.PaceMilliseconds > 0 ? CLI.Options.PaceMilliseconds : 50);
                    }
                }
                catch (Exception ex)
                {
                    Program.Log($"[SQS:{queueName}] {nameof(ReceivePump)}: {ex.Message}");
                    Delay.Wait(1000);
                }
            }
        }

        public override void Stop(string reason)
        {
            Program.Log($"{nameof(SqsTunnel)}: Stopping. Reason: {reason}");
        }
    }
}
