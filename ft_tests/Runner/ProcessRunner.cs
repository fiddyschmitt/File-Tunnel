using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ft_tests.Runner
{
    public abstract class ProcessRunner
    {
        public string RunOnIP { get; }

        public abstract string GetFullCommand(string args);

        public abstract void Run(string args);

        public abstract void Run(string cmd, string args);

        public abstract TimeSpan? Stop();

        public abstract void DeleteFile(string path);

        public ProcessRunner(string runOnIP)
        {
            RunOnIP = runOnIP;
        }
    }
}
