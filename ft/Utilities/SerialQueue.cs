using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ft.Utilities
{
    public class SerialQueue
    {
        private readonly BlockingCollection<Func<Task<bool>>> _work =
        new(new ConcurrentQueue<Func<Task<bool>>>());
        private readonly Thread _worker;

        public SerialQueue()
        {
            _worker = new Thread(async () =>
            {
                foreach (var job in _work.GetConsumingEnumerable())
                {
                    try
                    {
                        await job().ConfigureAwait(false);
                    }
                    catch
                    {

                    }
                }
            })
            { IsBackground = true };
            _worker.Start();
        }

        public Task<bool> Enqueue(Func<Task<bool>> job)
        {
            var tcs = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            _work.Add(async () =>
            {
                try
                {
                    var result = await job().ConfigureAwait(false);
                    tcs.SetResult(result);
                    return result;
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                    throw;
                }
            });

            return tcs.Task;
        }

        public void Dispose() => _work.CompleteAdding();
    }
}
