using ft.Bandwidth;
using ft.Commands;
using ft.Streams;
using ft.Utilities;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.RateLimiting;
using System.Threading.Tasks;

namespace ft.Listeners
{
    public abstract class SharedFileManager : StreamEstablisher
    {
        public EventHandler<OnlineStatusEventArgs>? OnlineStatusChanged;
        public EventHandler<CreateLocalListenerEventArgs>? CreateLocalListenerRequested;
        public EventHandler? SessionChanged;

        public string ReadFromFilename { get; }
        public string WriteToFilename { get; }

        protected readonly ConcurrentDictionary<int, BlockingCollection<byte[]>> ReceiveQueue = [];

        //If left unlimited, this can fill up faster than we can send them through the file tunnel.
        //FPS 22/08/2025: When this is set to 20, RDP latency is worse than when it is set to 1.
        protected BlockingCollection<Command> SendQueue = new(1);

        const int reportIntervalMs = 1000;
        public int TunnelTimeoutMilliseconds { get; protected set; }
        public bool Verbose { get; protected set; }
        DateTime? lastContactFromCounterpart;
        DateTime? lastSuccessfulPing;
        public bool IsOnline { get; protected set; } = false;


        public abstract void SendPump();

        public abstract void ReceivePump();

        public override abstract void Stop(string reason);

        public SharedFileManager(string readFromFilename, string writeToFilename, int tunnelTimeoutMilliseconds, bool verbose)
        {
            ReadFromFilename = readFromFilename;
            WriteToFilename = writeToFilename;
            TunnelTimeoutMilliseconds = tunnelTimeoutMilliseconds;
            Verbose = verbose;
        }

        public void Connect(int connectionId, string destinationEndpointStr)
        {
            var connectCommand = new Connect(connectionId, destinationEndpointStr);
            if (EnqueueToSend(connectCommand))
            {
                if (!ReceiveQueue.TryGetValue(connectionId, out _))
                {
                    ReceiveQueue.TryAdd(connectionId, []);
                }
            }
        }

        public override void Start()
        {
            Threads.StartNew(ReceivePump, nameof(ReceivePump));
            Threads.StartNew(SendPump, nameof(SendPump));
            Threads.StartNew(SendPingRequests, nameof(SendPingRequests));
            Threads.StartNew(ReportNetworkPerformance, nameof(ReportNetworkPerformance));
            Threads.StartNew(MonitorOnlineStatus, nameof(MonitorOnlineStatus));

            if (Debugger.IsAttached)
            {
                //Threads.StartNew(WriteThreadReport, nameof(WriteThreadReport));
            }
        }

        public int GenerateUniqueConnectionId()
        {
            int result;
            while (true)
            {
                result = Random.Shared.Next(int.MaxValue);

                if (!ReceiveQueue.ContainsKey(result))
                {
                    break;
                }
            }

            return result;
        }

        public bool EnqueueToSend(Command cmd, int timeoutMilliseconds)
        {
            bool result;
            try
            {
                result = SendQueue.TryAdd(cmd, timeoutMilliseconds);
            }
            catch (Exception ex)
            {
                Program.Log($"WARNING! Error while enqueing {cmd.GetType().Name}: {ex.Message}");
                result = false;
            }

            if (!result)
            {
                Program.Log($"WARNING! Could not enqueue {cmd.GetType().Name}");
            }

            return result;
        }

        public bool EnqueueToSend(Command cmd)
        {
            var result = EnqueueToSend(cmd, TunnelTimeoutMilliseconds);
            return result;
        }

        public byte[]? Read(int connectionId)
        {
            if (!ReceiveQueue.TryGetValue(connectionId, out var connectionReceiveQueue))
            {
                return null;
            }

            byte[]? result = null;
            try
            {
                result = connectionReceiveQueue.Take();
            }
            catch (InvalidOperationException)
            {
                //This is normal - the queue might have been marked as AddingComplete while we were listening
            }

            return result;
        }

        protected void CommandSent(Command command)
        {
            if (command is Forward forward && forward.Payload != null)
            {
                var totalBytesSent = sentBandwidth.TotalBytesTransferred + (ulong)forward.Payload.Length;
                sentBandwidth.SetTotalBytesTransferred(totalBytesSent);
            }

            if (command is Ping ping && ping.PingType == EnumPingType.Request)
            {
                lock (sentPingRequests)
                {
                    sentPingRequests.Add((DateTime.Now, ping));
                }
            }
        }

        protected void CommandReceived(Command command)
        {
            lastContactFromCounterpart = DateTime.Now;

            if (command is Forward forward && forward.Payload != null)
            {
                if (ReceiveQueue.TryGetValue(forward.ConnectionId, out var connectionReceiveQueue))
                {
                    connectionReceiveQueue.Add(forward.Payload);

                    var totalBytesReceived = receivedBandwidth.TotalBytesTransferred + (ulong)(forward.Payload.Length);
                    receivedBandwidth.SetTotalBytesTransferred(totalBytesReceived);
                }
            }
            else if (command is Connect connect)
            {
                if (!ReceiveQueue.ContainsKey(connect.ConnectionId))
                {
                    ReceiveQueue.TryAdd(connect.ConnectionId, []);

                    Threads.StartNew(() =>
                    {
                        var sharedFileStream = new SharedFileStream(this, connect.ConnectionId);
                        ConnectionAccepted?.Invoke(this, new ConnectionAcceptedEventArgs(sharedFileStream, connect.DestinationEndpointString));
                    }, "ConnectionAccepted");
                }
            }
            else if (command is CreateListener createListener)
            {
                var createLocalListenerEventArgs = new CreateLocalListenerEventArgs(createListener.Protocol, createListener.ForwardString);
                CreateLocalListenerRequested?.Invoke(this, createLocalListenerEventArgs);
            }
            else if (command is TearDown teardown && ReceiveQueue.TryGetValue(teardown.ConnectionId, out var connectionReceiveQueue))
            {
                Program.Log($"Counterpart asked to tear down connection {teardown.ConnectionId}");

                ReceiveQueue.Remove(teardown.ConnectionId, out _);

                connectionReceiveQueue.CompleteAdding();
            }
            else if (command is Ping ping)
            {
                if (ping.PingType == EnumPingType.Request)
                {
                    var response = new Ping(EnumPingType.Response)
                    {
                        ResponseToPacketNumber = ping.PacketNumber
                    };

                    Task.Factory.StartNew(() =>
                    {
                        //start in a new task, because we want to continue receiving messages while queuing this message may block
                        EnqueueToSend(response);
                    });
                }

                if (ping.PingType == EnumPingType.Response)
                {
                    lock (sentPingRequests)
                    {
                        var pingRequest = sentPingRequests.FirstOrDefault(sentPing => sentPing.Ping.PacketNumber == ping.ResponseToPacketNumber);

                        if (pingRequest != default)
                        {
                            latestRTT = DateTime.Now - pingRequest.DateSent;
                            lastSuccessfulPing = DateTime.Now;
                        }
                    }
                }
            }
        }

        public void TearDown(int connectionId)
        {
            var teardownCommand = new TearDown(connectionId);

            if (IsOnline)
            {
                EnqueueToSend(teardownCommand);
            }

            if (ReceiveQueue.TryGetValue(connectionId, out var receiveQueue))
            {
                receiveQueue.CompleteAdding();
                ReceiveQueue.TryRemove(connectionId, out _);
            }
        }

        public void TearDownAllConnections()
        {
            ReceiveQueue
                .Keys
                .ToList()
                .ForEach(connectionId =>
                {
                    TearDown(connectionId);
                });
        }

        private readonly List<(DateTime DateSent, Ping Ping)> sentPingRequests = [];

        public TimeSpan? latestRTT = null;
        public void SendPingRequests()
        {
            var pingRateLimiter = new FixedWindowRateLimiter(
                    new FixedWindowRateLimiterOptions()
                    {
                        PermitLimit = 1,
                        Window = TimeSpan.FromMilliseconds(1000),
                        QueueLimit = int.MaxValue,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst
                    });

            while (true)
            {
                try
                {
                    pingRateLimiter.Wait();

                    var pingRequest = new Ping(EnumPingType.Request);

                    EnqueueToSend(pingRequest, 1000);

                    lock (sentPingRequests)
                    {
                        sentPingRequests
                            .RemoveAll(ping =>
                            {
                                var timeSinceSent = DateTime.Now - ping.DateSent;
                                var remove = timeSinceSent.TotalMilliseconds > TunnelTimeoutMilliseconds;
                                return remove;
                            });
                    }
                }
                catch (Exception ex)
                {
                    Program.Log($"{nameof(SendPingRequests)}: {ex}");
                }
            }
        }

        readonly BandwidthTracker sentBandwidth = new(100, reportIntervalMs);
        readonly BandwidthTracker receivedBandwidth = new(100, reportIntervalMs);
        public void ReportNetworkPerformance()
        {
            while (true)
            {
                try
                {
                    var sentBandwidthStr = sentBandwidth.GetBandwidth();
                    var receivedBandwidthStr = receivedBandwidth.GetBandwidth();

                    lock (Program.ConsoleOutputLock)
                    {
                        Console.Write($"{DateTime.Now}  Counterpart: ");

                        if (IsOnline)
                        {
                            Console.ForegroundColor = ConsoleColor.Green;
                            Console.Write($"{"Online",-10}");

                            var logStr = $"Rx: {receivedBandwidthStr,-12} Tx: {sentBandwidthStr,-12}";


                            if (latestRTT != null)
                            {
                                logStr += $" {latestRTT.Value.TotalMilliseconds:N0} ms";
                                latestRTT = null;
                            }

                            Console.ForegroundColor = Program.OriginalConsoleColour;
                            Console.WriteLine(logStr);
                        }
                        else
                        {
                            Console.ForegroundColor = ConsoleColor.Red;
                            Console.Write($"{"Offline",-10}");

                            var offlineReason = "Counterpart is not responding.";

                            Console.ForegroundColor = Program.OriginalConsoleColour;
                            Console.WriteLine(offlineReason);
                        }
                    }

                    Delay.Wait(reportIntervalMs);
                }
                catch (Exception ex)
                {
                    Program.Log($"{nameof(ReportNetworkPerformance)}: {ex}");
                }
            }
        }

        private void MonitorOnlineStatus()
        {
            try
            {
                while (true)
                {
                    if (lastContactFromCounterpart != null && lastSuccessfulPing != null)
                    {
                        var orig = IsOnline;

                        var timeSinceLastContact = DateTime.Now - lastContactFromCounterpart.Value;
                        var timeSinceLastSuccessfulPing = DateTime.Now - lastSuccessfulPing.Value;

                        IsOnline = timeSinceLastContact.TotalMilliseconds < TunnelTimeoutMilliseconds && timeSinceLastSuccessfulPing.TotalMilliseconds < TunnelTimeoutMilliseconds;

                        if (orig != IsOnline)
                        {
                            OnlineStatusChanged?.Invoke(this, new OnlineStatusEventArgs(IsOnline));
                        }
                    }

                    Delay.Wait(100);
                }
            }
            catch (Exception ex)
            {
                Program.Log($"{nameof(MonitorOnlineStatus)}: {ex.Message}");
            }
        }

        public static void WriteThreadReport()
        {
            while (true)
            {
                var threads = Threads
                                    .CreatedThreads
                                    .Where(thread => thread.ThreadState != System.Threading.ThreadState.Stopped)
                                    .OrderBy(thread => thread.ThreadState)
                                    .ThenBy(thread => thread.Name)
                                    .Where(thread => !string.IsNullOrEmpty(thread.Name))
                                    .ToList();

                var threadStr = threads
                                    .Select((thread, index) => $"{index + 1:N0}/{threads.Count:N0} [{thread.ThreadState}] (Id {thread.ManagedThreadId}) {thread.Name}")
                                    .ToString(Environment.NewLine);

                Console.WriteLine(threadStr);

                Delay.Wait(10000);
            }
        }
    }

    public class OnlineStatusEventArgs(bool isOnline)
    {
        public bool IsOnline { get; } = isOnline;
    }

    public class CreateLocalListenerEventArgs(string protocol, string forwardString)
    {
        public string Protocol { get; } = protocol;
        public string ForwardString { get; } = forwardString;
    }
}
