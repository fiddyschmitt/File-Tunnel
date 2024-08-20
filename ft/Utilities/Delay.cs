using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ft.Utilities
{
    public static class Delay
    {
        public static void Wait(int ms)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                WindowsDelay.Wait(ms);
            }
            else
            {
                Thread.Sleep(ms);
            }
        }
    }
}
