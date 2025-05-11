using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ft_tests.Runner
{
    public abstract class ProcessRunner
    {
        public string RunOnIP { get; }

        public abstract void Run(string args);

        public abstract void Stop();

        public ProcessRunner(string runOnIP)
        {
            RunOnIP = runOnIP;
        }
    }
}
