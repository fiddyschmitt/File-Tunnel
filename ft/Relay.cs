using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ft
{
    public class Relay
    {
        public EventHandler? RelayFinished;
        bool Stopped = false;

        public Relay(Stream fromStream, Stream toStream, long maxFileSizeBytes, int readDurationMillis)
        {
            //FPS 06/09/2025: Using 64 KB because it's likely the largest size that can be atomically written by both SMB and NFS.
            //Unverified though.
            //NFSv2 max 8 KB
            //NFSv3 max 64 KB
            //NFSv4 max between 64 KB and 1 MB
            //SMB 1.x max 64 KB
            //SMB 2.x max 8 MB
            //SMB 3.x max 8 MB

            var bufferSize = 65535;

            var bytesToRead = bufferSize;
            if (maxFileSizeBytes > 0)
            {
                bytesToRead = (int)(maxFileSizeBytes * 0.9);        //leave some room for commands like Purge
                bytesToRead = Math.Min(bufferSize, bytesToRead);
            }

            //var filename = $"";
            //var i = 1;

            //while (true)
            //{
            //    filename = $"{Environment.ProcessId} - {i}.dat";
            //    if (!File.Exists(filename))
            //    {
            //        break;
            //    }

            //    i++;
            //}

            //var fs = File.Create(filename);

            Threads.StartNew(() =>
            {
                try
                {
                    Extensions.CopyTo(fromStream, toStream, bufferSize, bytesRead =>
                    {
                        if (bytesRead > 0)
                        {
                            //Program.Log($"{fromStream.Name(true)} -> {toStream.Name(false)}    {bytesRead:N0} bytes.");
                        }
                    }, null, readDurationMillis);
                }
                catch (Exception ex)
                {
                    if (!Stopped)
                    {
                        Program.Log($"{fromStream} -> {toStream}: {ex.Message}");
                    }
                }

                //fs.Close();

                RelayFinished?.Invoke(this, new EventArgs());
            }, $"{fromStream.Name(true)} -> {toStream.Name(false)}");

            FromStream = fromStream;
            ToStream = toStream;
        }

        public Stream FromStream { get; }
        public Stream ToStream { get; }

        public void Stop()
        {
            if (Stopped) return;

            Stopped = true;

            try
            {
                FromStream.Close();
            }
            catch
            {
                //Program.Log($"Stop(): {ex}");
            }


            try
            {
                ToStream.Close();
            }
            catch
            {
                //Program.Log($"Stop(): {ex}");
            }



            Program.Log($"Closed relay. {FromStream.Name(true)} -> {ToStream.Name(false)}");
        }
    }
}
