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

        public Relay(Stream fromStream, Stream toStream, long writeFileSize, int readDurationMillis)
        {
            var bufferSize = (int)(writeFileSize / 2d * 0.9d);
            bufferSize = Math.Max(bufferSize, 1024 * 1024);

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
                        Program.Log($"{fromStream} -> {toStream}: {ex}");
                    }
                }

                RelayFinished?.Invoke(this, new EventArgs());
            }, $"{fromStream.Name(true)} -> {toStream.Name(false)}");

            FromStream = fromStream;
            ToStream = toStream;
        }

        public Stream FromStream { get; }
        public Stream ToStream { get; }

        public void Stop()
        {
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
