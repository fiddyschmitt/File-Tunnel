using ft.Bandwidth;
using ft.Commands;
using ft.Streams;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
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

        //Buffer a maximum of 20 messages to send.
        //If left unlimited, this can fill up faster than we can send them through the file tunnel.
        protected BlockingCollection<Command> SendQueue = new(20);

        const int reportIntervalMs = 1000;
        public int TunnelTimeoutMilliseconds { get; protected set; }
        public bool Verbose { get; protected set; }
        DateTime? lastContactFromCounterpart;
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
            Threads.StartNew(MeasureRTT, nameof(MeasureRTT));
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
                result = Program.Random.Next(int.MaxValue);

                if (!ReceiveQueue.ContainsKey(result))
                {
                    break;
                }
            }

            return result;
        }

        public bool EnqueueToSend(Command cmd)
        {
            bool result;
            try
            {
                result = SendQueue.TryAdd(cmd, TunnelTimeoutMilliseconds);
            }
            catch
            {
                Program.Log($"WARNING! Could not enqueue {cmd.GetType().Name}");
                result = false;
            }

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
                    pingResponsesReceived.Add(ping);
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

        readonly BlockingCollection<Ping> pingResponsesReceived = [];
        public TimeSpan? latestRTT = null;
        public void MeasureRTT()
        {
            var pingRequest = new Ping(EnumPingType.Request);
            var pingStopwatch = new Stopwatch();

            while (true)
            {
                try
                {
                    pingStopwatch.Restart();

                    if (!EnqueueToSend(pingRequest))
                    {
                        latestRTT = null;
                        continue;
                    }

                    try
                    {
                        while (true)
                        {
                            if (pingResponsesReceived.TryTake(out Ping? pingResponse, 100))
                            {
                                if (pingRequest.PacketNumber == pingResponse.ResponseToPacketNumber)
                                {
                                    pingStopwatch.Stop();

                                    latestRTT = pingStopwatch.Elapsed;
                                    break;
                                }
                            }

                            if (pingStopwatch.ElapsedMilliseconds > TunnelTimeoutMilliseconds)
                            {
                                latestRTT = null;
                                break;
                            }
                        }
                    }
                    catch
                    {
                        latestRTT = null;
                        continue;
                    }

                    var durationToSleep = (int)(1000 - latestRTT?.TotalMilliseconds ?? 0);
                    if (durationToSleep < 0) durationToSleep = 0;
                    Thread.Sleep(durationToSleep);
                }
                catch (Exception ex)
                {
                    Program.Log($"{nameof(MeasureRTT)}: {ex}");
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
                        Console.Write($"{DateTime.Now}: Counterpart: ");

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

                    Thread.Sleep(reportIntervalMs);
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
                    if (lastContactFromCounterpart != null)
                    {
                        var orig = IsOnline;

                        var timeSinceLastContact = DateTime.Now - lastContactFromCounterpart.Value;
                        IsOnline = timeSinceLastContact.TotalMilliseconds < TunnelTimeoutMilliseconds;

                        if (orig != IsOnline)
                        {
                            OnlineStatusChanged?.Invoke(this, new OnlineStatusEventArgs(IsOnline));
                        }
                    }

                    Thread.Sleep(100);
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

                Thread.Sleep(10000);
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
