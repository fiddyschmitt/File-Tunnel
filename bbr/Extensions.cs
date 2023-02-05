using bbr.Streams;
using System;
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
        public static void CopyTo(this Stream input, Stream output, int bufferSize)
        {
            var buffer = new byte[bufferSize];
            while (true)
            {
                var read = 0;

                try
                {
                    read = input.Read(buffer, 0, buffer.Length);
                    output.Write(buffer, 0, read);
                }
                catch { }

                if (read == 0) break;
            }
        }

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


        static Dictionary<Stream, (string ReadString, string WriteString)> StreamNames = new();
        public static string Name(this Stream stream, bool readFrom)
        {
            if (!StreamNames.ContainsKey(stream))
            {
                var name = stream.GetType().Name;

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

            var streamNames = StreamNames[stream];

            var streamName = readFrom ? streamNames.ReadString : streamNames.WriteString;

            return streamName;

            
        }
    }
}
