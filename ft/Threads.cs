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
                action();
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
