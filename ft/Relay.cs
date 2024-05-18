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
            var bufferSize = (int)((Program.SHARED_FILE_SIZE / 2d) * 0.9d);

            Task.Factory.StartNew(() =>
            {
                try
                {
                    Extensions.CopyTo(fromStream, toStream, bufferSize, bytesRead =>
                    {
                        if (bytesRead > 0)
                        {
                            //Program.Log($"{fromStream.Name(true)} -> {toStream.Name(false)}    {bytesRead:N0} bytes.");
                        }
                    }, null);
                }
                catch (Exception ex)
                {
                    if (!Stopped)
                    {
                        Program.Log($"{fromStream} -> {toStream}: {ex}");
                    }
                }

                RelayFinished?.Invoke(this, new EventArgs());
            }, TaskCreationOptions.LongRunning);
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
            catch (Exception ex)
            {
                Program.Log($"Stop(): {ex}");
            }


            try
            {
                ToStream.Close();
            }
            catch (Exception ex)
            {
                Program.Log($"Stop(): {ex}");
            }



            Program.Log($"Closed relay. {FromStream.Name(true)} -> {ToStream.Name(false)}");
        }
    }
}
