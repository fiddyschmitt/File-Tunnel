﻿using System;
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

        public Relay(Stream fromStream, Stream toStream, long maxFileSizeBytes)
        {
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
                    var buffer = new byte[65535 * 2];

                    while (true)
                    {
                        var read = fromStream.Read(buffer, 0, bytesToRead);

                        if (read == 0)
                        {
                            break;
                        }

                        //fs.Write(buffer, 0, read);
                        //fs.Flush();

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
