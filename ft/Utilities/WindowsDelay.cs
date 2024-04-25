/*
*MIT License
*
*Copyright (c) 2023 S Christison
*
*Permission is hereby granted, free of charge, to any person obtaining a copy
*of this software and associated documentation files (the "Software"), to deal
*in the Software without restriction, including without limitation the rights
*to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
*copies of the Software, and to permit persons to whom the Software is
*furnished to do so, subject to the following conditions:
*
*The above copyright notice and this permission notice shall be included in all
*copies or substantial portions of the Software.
*
*THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
*IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
*FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
*AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
*LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
*OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
*SOFTWARE.
*/

using System.Runtime.InteropServices;

namespace System.Threading
{
    /// <summary>
    /// Platform Dependent Wait
    /// Accurately wait down to 1ms if your platform will allow it
    /// Wont murder your CPU
    public static partial class WindowsDelay
    {
        internal const string windowsMultimediaAPIString = "winmm.dll";

        [LibraryImport(windowsMultimediaAPIString)]
        internal static partial int timeBeginPeriod(int period);

        [LibraryImport(windowsMultimediaAPIString)]
        internal static partial int timeEndPeriod(int period);

        [LibraryImport(windowsMultimediaAPIString)]
        internal static partial int timeGetDevCaps(ref TimerCapabilities caps, int sizeOfTimerCaps);

        internal static TimerCapabilities Capabilities;

        static WindowsDelay()
        {
            _ = timeGetDevCaps(ref Capabilities, Marshal.SizeOf(Capabilities));
        }

        /// <summary>
        /// Platform Dependent Wait
        /// Accurately wait down to 1ms if your platform will allow it
        /// Wont murder your CPU
        /// </summary>
        /// <param name="delayMs"></param>
        public static void Wait(int delayMs)
        {
            _ = timeBeginPeriod(Capabilities.PeriodMinimum);
            Thread.Sleep(delayMs);
            _ = timeEndPeriod(Capabilities.PeriodMinimum);
        }

        /// <summary>
        /// The Min/Max supported period for the Mutlimedia Timer in milliseconds
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct TimerCapabilities
        {
            /// <summary>Minimum supported period in milliseconds.</summary>
            public int PeriodMinimum;

            /// <summary>Maximum supported period in milliseconds.</summary>
            public int PeriodMaximum;
        }
    }
}