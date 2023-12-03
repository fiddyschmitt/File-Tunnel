using bbr.Streams;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace bbr
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

        public static void CopyTo(this Stream input, Stream output, int bufferSize, Action<int> callBack, CancellationTokenSource cancellationTokenSource)
        {
            var buffer = BufferPool.Rent(bufferSize);

            var read = 0;
            while (true)
            {
                if (cancellationTokenSource != null && cancellationTokenSource.IsCancellationRequested)
                {
                    break;
                }

                read = input.Read(buffer, 0, bufferSize);

                if (read == 0)
                {
                    Program.Log("Nothing left to read");
                    break;
                }

                output.Write(buffer, 0, read);

                callBack?.Invoke(read);
            }
            callBack?.Invoke(read);

            BufferPool.Return(buffer);
        }


        static readonly Dictionary<Stream, (string ReadString, string WriteString)> StreamNames = new();
        public static string Name(this Stream stream, bool readFrom)
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
    }
}
