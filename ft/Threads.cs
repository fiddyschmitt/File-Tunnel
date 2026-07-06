using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ft
{
    public static class Threads
    {
        public readonly static ConcurrentBag<Thread> CreatedThreads = [];

        public static Thread StartNew(Action action, string threadName)
        {
            var thread = new Thread(() =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    // A fire-and-forget worker throwing must not take down the whole process. The pumps do
                    // their own restart-on-error; this is the safety net for event handlers (e.g. the
                    // ConnectionAccepted handler) that run on these threads - one malformed frame or a DNS
                    // hiccup should drop that connection, not crash ft.
                    Program.Log($"Thread '{threadName}' terminated with an unhandled exception: {ex.Message}");
                }
            })
            {
                Name = $"[FT] {threadName}",
                IsBackground = true
            };
            thread.Start();

            if (Debugger.IsAttached)
            {
                CreatedThreads.Add(thread);
            }

            return thread;
        }
    }
}
