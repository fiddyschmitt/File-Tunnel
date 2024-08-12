using ft.CLI;
using ft.Listeners;
using ft.Streams;
using ft.Utilities;
using CommandLine;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics.CodeAnalysis;

namespace ft
{
    public class Program
    {
        const string PROGRAM_NAME = "File Tunnel";
        const string VERSION = "2.2.0";


        static int connectionId = 0;

        [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(Options))]
        public static void Main(string[] args)
        {
            Log($"{PROGRAM_NAME} {VERSION}");

            var parser = new Parser(settings =>
            {
                settings.AllowMultiInstance = true;
                settings.AutoHelp = true;
                settings.HelpWriter = Console.Out;
                settings.AutoVersion = true;
            });

            parser.ParseArguments<Options>(args)
               .WithParsed(o =>
               {
                   var sharedFileManager = new SharedFileManager(o.ReadFrom.Trim(), o.WriteTo.Trim(), o.PurgeSizeInBytes, o.TunnelTimeoutMilliseconds);

                   if (o.TcpForwards.Any() || o.UdpForwards.Any())
                   {
                       var listener = new MultiServer();
                       if (o.TcpForwards.Any()) listener.Add("tcp", o.TcpForwards.ToList());
                       if (o.UdpForwards.Any()) listener.Add("udp", o.UdpForwards.ToList());

                       if (listener == null)
                       {
                           Log("No listener specified by args");
                           return;
                       }

                       sharedFileManager.OnlineStatusChanged += (sender, args) =>
                       {
                           if (args.IsOnline)
                           {
                               listener.Start();
                           }
                           else
                           {
                               listener.Stop();
                               sharedFileManager.TearDownAllConnections();
                           }
                       };

                       if (listener == null) return;

                       listener.StreamEstablished += (sender, establishedArgs) =>
                       {
                           var cId = Interlocked.Increment(ref connectionId);
                           var secondaryStream = new SharedFileStream(sharedFileManager, cId);
                           secondaryStream.EstablishConnection(establishedArgs.DestinationEndpointString);

                           var relay1 = new Relay(establishedArgs.Stream, secondaryStream, o.PurgeSizeInBytes, o.ReadDurationMillis);
                           var relay2 = new Relay(secondaryStream, establishedArgs.Stream, o.PurgeSizeInBytes, o.ReadDurationMillis);

                           void tearDown()
                           {
                               relay1.Stop();
                               relay2.Stop();
                           }

                           relay1.RelayFinished += (s, a) => tearDown();
                           relay2.RelayFinished += (s, a) => tearDown();
                       };
                   }

                   sharedFileManager.OnlineStatusChanged += (sender, args) =>
                   {
                       if (!args.IsOnline)
                       {
                           sharedFileManager.TearDownAllConnections();
                       }
                   };

                   sharedFileManager.StreamEstablished += (sender, establishedArgs) =>
                   {
                       var connectToTokens = establishedArgs.DestinationEndpointString.Split(["://"], StringSplitOptions.None);
                       var protocol = connectToTokens[0];
                       var destinationEndpointStr = connectToTokens[1];

                       var destinationEndpoint = destinationEndpointStr.AsEndpoint();

                       if (protocol.Equals("tcp"))
                       {
                           try
                           {
                               var tcpClient = new TcpClient();
                               tcpClient.Connect(destinationEndpoint);


                               if (tcpClient.Connected)
                               {
                                   Log($"Connected to {destinationEndpointStr}");

                                   var relay1 = new Relay(tcpClient.GetStream(), establishedArgs.Stream, o.PurgeSizeInBytes, o.ReadDurationMillis);
                                   var relay2 = new Relay(establishedArgs.Stream, tcpClient.GetStream(), o.PurgeSizeInBytes, o.ReadDurationMillis);

                                   void tearDown()
                                   {
                                       relay1.Stop();
                                       relay2.Stop();
                                   }

                                   relay1.RelayFinished += (s, a) => tearDown();
                                   relay2.RelayFinished += (s, a) => tearDown();
                               }
                               else
                               {
                                   Log($"Could not connect to: {destinationEndpointStr}");
                               }
                           }
                           catch (Exception ex)
                           {
                               Log($"Error during connection to {destinationEndpointStr}. {ex.Message}");
                           }
                       }

                       if (protocol.Equals("udp"))
                       {
                           var sendFromEndpoint = o.UdpSendFrom.AsEndpoint();

                           var udpClient = new UdpClient();
                           udpClient.Client.Bind(sendFromEndpoint);

                           var udpStream = new UdpStream(udpClient, destinationEndpoint);

                           var relay1 = new Relay(udpStream, establishedArgs.Stream, o.PurgeSizeInBytes, o.ReadDurationMillis);
                           var relay2 = new Relay(establishedArgs.Stream, udpStream, o.PurgeSizeInBytes, o.ReadDurationMillis);

                           void tearDown()
                           {
                               relay1.Stop();
                               relay2.Stop();
                           }

                           relay1.RelayFinished += (s, a) => tearDown();
                           relay2.RelayFinished += (s, a) => tearDown();
                       }
                   };

                   sharedFileManager.Start();
               })
               .WithNotParsed(o =>
               {
                   Environment.Exit(1);
               });

            while (true)
            {
                try
                {
                    Thread.Sleep(1000);
                }
                catch
                {
                    break;
                }
            }
        }

        public static readonly ConsoleColor OriginalConsoleColour = Console.ForegroundColor;
        public static readonly Random Random = new();

        public static readonly object ConsoleOutputLock = new();

        public static void Log(string str, ConsoleColor? color = null)
        {
            lock (ConsoleOutputLock)
            {
                // Change color if specified
                if (color.HasValue)
                {
                    Console.ForegroundColor = color.Value;
                }

                Console.WriteLine($"{DateTime.Now}: {str}");

                // Reset to original color
                Console.ForegroundColor = OriginalConsoleColour;
            }
        }
    }
}
