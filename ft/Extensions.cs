using ft.Streams;
using ft.Utilities;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
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
                    read = inputNetworkStream.Read(buffer, 0, bufferSize, readDurationMillis);
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

        public static int Read(this NetworkStream input, byte[] buffer, int offset, int count, int readDurationMillis)
        {
            var stopwatch = new Stopwatch();

            var totalBytesRead = 0;
            var currentOffset = offset;

            while (true)
            {
                if (input.DataAvailable)
                {
                    if (!stopwatch.IsRunning)
                    {
                        stopwatch.Start();
                    }

                    var toRead = Math.Min(count - totalBytesRead, buffer.Length - currentOffset);

                    if (toRead <= 0)
                    {
                        break;
                    }

                    var bytesRead = input.Read(buffer, currentOffset, toRead);

                    currentOffset += bytesRead;
                    totalBytesRead += bytesRead;
                }
                else
                {
                    Delay.Wait(1);
                }

                if (totalBytesRead > 0 && stopwatch.IsRunning && stopwatch.ElapsedMilliseconds > readDurationMillis)
                {
                    break;
                }
            }

            stopwatch.Stop();

            return totalBytesRead;
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
    }
}
