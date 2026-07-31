using System.Runtime.InteropServices;
using System.Threading;

namespace ft.Utilities;

// TODO remove and use Task.Delay
public static class Delay
{
    public static void Wait(int ms)
    {
        if (ms == 0) return;

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