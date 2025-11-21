using ft.CLI;
﻿using ft.Commands;
using ft.Streams;
using ft.Utilities;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.RateLimiting;
using System.Threading.Tasks;

namespace ft
{
    public static class Extensions
    {
        /*
        public static void CopyTo(this Stream input, Stream output, int bufferSize, Action<int> callBack, CancellationTokenSource cancellationTokenSource)
        {
            var buffer = new byte[bufferSize];

            int read;
            while ((read = input.Read(buffer, 0, bufferSize)) > 0)
            {
                if (cancellationTokenSource != null && cancellationTokenSource.IsCancellationRequested)
                {
                    break;
                }

                output.Write(buffer, 0, read);

                callBack?.Invoke(read);
            }
            callBack?.Invoke(read);
        }
        */

        public const int ARBITARY_MEDIUM_SIZE_BUFFER = 5 * 1024 * 1024;

        //Initialised to something big, because otherwise it defaults to 1MB and smaller.
        //See: https://adamsitnik.com/Array-Pool/
        //Always remember to return the array back into the pool.
        //Never trust buffer.Length
        public static readonly ArrayPool<byte> BufferPool = ArrayPool<byte>.Create(ARBITARY_MEDIUM_SIZE_BUFFER + 1, 50);

        public static void CopyTo(this Stream input, Stream output, int bufferSize, Action<int> callBack, CancellationTokenSource? cancellationTokenSource, int readDurationMillis)
        {
            var buffer = BufferPool.Rent(bufferSize);

            //optimisation to get good responsiveness, and good bandwidth when there's lots of incoming data
            var maxQuietDurationMillis = (int)Math.Max(1, readDurationMillis / 4d);

            var read = 0;
            while (true)
            {
                if (cancellationTokenSource != null && cancellationTokenSource.IsCancellationRequested)
                {
                    break;
                }

                if (input is not NetworkStream inputNetworkStream || readDurationMillis <= 0)
                {
                    read = input.Read(buffer, 0, bufferSize);
                }
                else
                {
                    //Speed optimisation.
                    //We want to avoid writing tiny amounts of data to file, because IO is expensive. Let's accumulate n milliseconds worth of data.
                    read = inputNetworkStream.Read(buffer, 0, bufferSize, readDurationMillis, maxQuietDurationMillis);
                }

                if (read == 0)
                {
                    break;
                }

                output.Write(buffer, 0, read);

                callBack?.Invoke(read);
            }
            callBack?.Invoke(read);

            BufferPool.Return(buffer);
        }

        public static string GetName(this Command command)
        {
            string result;
            if (command is Ping p)
            {
                if (p.PingType == EnumPingType.Request)
                {
                    result = $"Ping request";
                }
                else
                {
                    result = $"Ping response for {p.ResponseToPacketNumber}";
                }
            }
            else
            {
                result = $"{command.GetType().Name}";
            }

            return result;
        }

        public static string GetMD5(this byte[] data)
        {
            var stream = new MemoryStream(data);
            var result = stream.GetMD5();
            return result;
        }

        public static string GetMD5(this Stream stream)
        {
            stream.Seek(0, SeekOrigin.Begin);

            using var md5Instance = System.Security.Cryptography.MD5.Create();
            var hashResult = md5Instance.ComputeHash(stream);

            var result = BitConverter.ToString(hashResult).Replace("-", "").ToLowerInvariant(); ;
            return result;
        }

        public static int Read(this NetworkStream input, byte[] buffer, int offset, int count, int maxDurationMillis, int maxQuietDurationMillis)
        {
            var totalTime = new Stopwatch();
            var timeSinceLastRead = new Stopwatch();

            var totalBytesRead = 0;
            var currentOffset = offset;

            while (true)
            {
                if (input.DataAvailable)
                {
                    if (!totalTime.IsRunning)
                    {
                        totalTime.Start();
                    }

                    var toRead = Math.Min(count - totalBytesRead, buffer.Length - currentOffset);

                    if (toRead <= 0)
                    {
                        break;
                    }

                    var bytesRead = input.Read(buffer, currentOffset, toRead);

                    timeSinceLastRead.Restart();

                    currentOffset += bytesRead;
                    totalBytesRead += bytesRead;
                }
                else
                {
                    Delay.Wait(1);
                }

                if (totalBytesRead > 0 && timeSinceLastRead.IsRunning && timeSinceLastRead.ElapsedMilliseconds > maxQuietDurationMillis)
                {
                    break;
                }

                if (totalBytesRead > 0 && totalTime.IsRunning && totalTime.ElapsedMilliseconds > maxDurationMillis)
                {
                    break;
                }

                if (!input.Socket.SocketConnected())
                {
                    break;
                }
            }

            totalTime.Stop();

            return totalBytesRead;
        }

        public static bool SocketConnected(this Socket s)
        {
            var part1 = s.Poll(1000, SelectMode.SelectRead);
            var part2 = s.Available == 0;
            if (part1 && part2)
            {
                return false;
            }
            else
            {
                return true;
            }
        }

        static readonly Dictionary<Stream, (string ReadString, string WriteString)> StreamNames = [];
        public static string Name(this Stream stream, bool readFrom)
        {
            try
            {
                if (!StreamNames.ContainsKey(stream))
                {
                    if (stream is UdpStream udpStream)
                    {
                        StreamNames.Add(
                                stream,
                                ($"{udpStream.SendTo} -> {udpStream.Client.Client.LocalEndPoint}",
                                $"{udpStream.Client.Client.LocalEndPoint} -> {udpStream.SendTo}"));
                    }

                    if (stream is NetworkStream networkStream)
                    {
                        StreamNames.Add(
                                stream,
                                ($"{networkStream.Socket.RemoteEndPoint} -> {networkStream.Socket.LocalEndPoint}",
                                 $"{networkStream.Socket.LocalEndPoint} -> {networkStream.Socket.RemoteEndPoint}"));
                    }


                    if (stream is SharedFileStream sharedFileStream)
                    {
                        StreamNames.Add(
                                stream,
                                (Path.GetFileName(sharedFileStream.SharedFileManager.ReadFromFilename),
                                 Path.GetFileName(sharedFileStream.SharedFileManager.WriteToFilename)));
                    }
                }

                var (ReadString, WriteString) = StreamNames[stream];

                var streamName = readFrom ? ReadString : WriteString;

                return streamName;
            }
            catch (Exception)
            {
                return "Unknown stream";
            }
        }

        public static string BytesToString(this uint bytes)
        {
            var result = BytesToString((ulong)bytes);
            return result;
        }

        public static string BytesToString(this int bytes)
        {
            var result = BytesToString((ulong)bytes);
            return result;
        }

        public static string BytesToString(this long bytes)
        {
            var result = BytesToString((ulong)bytes);
            return result;
        }

        public static string BytesToString(this ulong bytes)
        {
            string[] UNITS = ["B", "KB", "MB", "GB", "TB", "PB", "EB"];
            int c;
            for (c = 0; c < UNITS.Length; c++)
            {
                ulong m = (ulong)1 << ((c + 1) * 10);
                if (bytes < m)
                    break;
            }

            double n = bytes / (double)((ulong)1 << (c * 10));
            return string.Format("{0:0.##} {1}", n, UNITS[c]);
        }

        public static bool IsValidEndpoint(this string endpointStr)
        {
            var result = false;

            if (!string.IsNullOrEmpty(endpointStr))
            {
                if (IPEndPoint.TryParse(endpointStr, out var ep))
                {
                    if (ep.Port > 0)
                    {
                        result = true;
                    }
                }
                else
                {
                    var tokens = endpointStr.Split([":"], StringSplitOptions.None);
                    if (tokens.Length == 2 && int.TryParse(tokens[1], out var _))
                    {
                        result = true;
                    }
                }
            }

            return result;
        }

        public static string ToString(this IEnumerable<string> list, string seperator)
        {
            var result = string.Join(seperator, list);
            return result;
        }

        public static bool IsIPV6(this string ipStr)
        {
            var result = IPAddress.TryParse(ipStr, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6;
            return result;
        }

        public static string WrapIfIPV6(this string ipStr)
        {
            var result = ipStr.IsIPV6() ? $"[{ipStr}]" : ipStr;
            return result;
        }

        public static IPEndPoint ToIpEndpoint(this EndPoint endPoint)
        {
            if (endPoint is IPEndPoint ipEndpoint)
            {
                return ipEndpoint;
            }

            if (endPoint is DnsEndPoint dnsEndPoint)
            {
                var addresses = Dns.GetHostAddresses(dnsEndPoint.Host);
                if (addresses == null || addresses.Length == 0)
                {
                    throw new Exception($"Unable to retrieve IP address from specified host name: {dnsEndPoint.Host}");
                }

                return new IPEndPoint(addresses[0], dnsEndPoint.Port);
            }

            throw new Exception($"Unhandled Endpoint type: {endPoint.GetType()}");
        }

        public static IPEndPoint AsEndpoint(this string endpointStr)
        {
            var endpoint = NetworkUtilities.ParseEndpoint(endpointStr);
            var result = endpoint.ToIpEndpoint();
            return result;
        }

        public static (int Attempts, TimeSpan Duration) Time(
            string operation,
            Func<(int Attempt, TimeSpan Elapsed), bool> action,
            Func<(int Attempt, TimeSpan Elapsed, string Operation), int> getSleepDurationMillis,
            bool printOutput)
        {
            printOutput &= Debugger.IsAttached;


            if (printOutput)
            {
                Program.Log($"Started {operation}");
            }

            var sw = new Stopwatch();
            sw.Start();

            var attempt = 1;
            while (true)
            {
                if (printOutput)
                {
                    Program.Log($"{operation} attempt {attempt:N0}");
                }

                var finished = action((attempt, sw.Elapsed));

                if (finished)
                {
                    break;
                }

                var sleepMillis = getSleepDurationMillis((attempt, sw.Elapsed, operation));

                Delay.Wait(sleepMillis);

                attempt++;
            }

            sw.Stop();

            if (printOutput && sw.ElapsedMilliseconds > 1000)
            {
                Program.Log($"{operation} took {attempt:N0} attempts ({sw.Elapsed.TotalSeconds:N3} seconds)");
            }

            if (printOutput)
            {
                Program.Log($"{operation} took {attempt:N0} attempts ({sw.Elapsed.TotalSeconds:N3} seconds)");
            }

            return (attempt, sw.Elapsed);
        }

        public static void Retry(string operation, Action action, bool verbose, int timeoutMilliseconds)
        {
            Time(
                operation,
                (attempt) =>
                {
                    try
                    {
                        action();
                    }
                    catch
                    {
                        return false;
                    }

                    return true;
                },
                (attempt) =>
                {
                    if (attempt.Elapsed.TotalMilliseconds > timeoutMilliseconds)
                    {
                        throw new Exception($"Timeout during {attempt.Operation}");
                    }

                    return 10;
                },
                verbose);
        }

        public static void Flush(this Stream stream, bool verbose, int timeoutMilliseconds)
        {
            if (Options.Citrix && stream is FileStream fileStream)
            {
                Retry($"Flush to disk", () => fileStream.Flush(true), verbose, timeoutMilliseconds);
            }
            else
            {
                Retry($"{nameof(stream)}.{nameof(Stream.Flush)}", stream.Flush, verbose, timeoutMilliseconds);
            }
        }

        public static void Flush(this BinaryWriter binaryWriter, bool verbose, int timeoutMilliseconds)
        {
            Retry($"{nameof(BinaryWriter)}.{nameof(BinaryWriter.Flush)}", binaryWriter.Flush, verbose, timeoutMilliseconds);

            if (binaryWriter.BaseStream != null)
            {
                Flush(binaryWriter.BaseStream, verbose, timeoutMilliseconds);
            }
        }

        public static void ForceRead(this Stream stream, int tunnelTimeoutMilliseconds, bool verbose)
        {
            if (stream is FileStream fileStream)
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    using var tempFs = new FileStream(fileStream.Name, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    tempFs.Read(new byte[4096]);
                }
                else
                {
                    fileStream.Flush(verbose, tunnelTimeoutMilliseconds);
                }
            }
            else
            {
                stream.Flush(verbose, tunnelTimeoutMilliseconds);
            }
        }

        public static void Wait(this ReplenishingRateLimiter? limiter)
        {
            limiter?.AcquireAsync(1, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        }
    }
}
