using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace bbr
{
    public class Relay
    {
        public EventHandler RelayFinished;
        bool Stopped = false;

        public Relay(Stream fromStream, Stream toStream)
        {
            Task.Factory.StartNew(() =>
            {

                try
                {
                    //fromStream.CopyTo(toStream);
                    Extensions.CopyTo(fromStream, toStream, 131072, (bytesRead) =>
                    {
                        Program.Log($"{fromStream.Name(true)} -> {toStream.Name(false)}    {bytesRead:N0} bytes.");
                    }, null);
                }
                catch (Exception ex)
                {
                    if (!Stopped)
                    {
                        Program.Log($"{fromStream} -> {toStream}: {ex}");
                    }
                }

                RelayFinished?.Invoke(this, null);
            });
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



            Program.Log($"Closed relay. {FromStream.Name(true)} -> {ToStream.Name(true)}");
        }
    }
}
