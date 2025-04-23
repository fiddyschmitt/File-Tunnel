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

        public Relay(Stream fromStream, Stream toStream)
        {
            var bufferSize = 65535;

            Threads.StartNew(() =>
            {
                try
                {
                    var buffer = new byte[65535 * 2];

                    while (true)
                    {
                        var read = fromStream.Read(buffer, 0, bufferSize);

                        if (read == 0)
                        {
                            break;
                        }

                        toStream.Write(buffer, 0, read);
                    }
                }
                catch (Exception ex)
                {
                    if (!Stopped)
                    {
                        Program.Log($"{fromStream} -> {toStream}: {ex.Message}");
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
