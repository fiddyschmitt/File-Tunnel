using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ft
{
    public static class Threads
    {
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

            return thread;
        }
    }
}
