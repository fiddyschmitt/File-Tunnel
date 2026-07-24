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

        // Runs an arbitrary command ON this node and BLOCKS for its result (exit code + combined
        // stdout/stderr). Used by the SOCKS end-to-end test to drive a real client (curl) against the
        // node's local SOCKS proxy. Distinct from Run(): that launches ft detached; this waits + captures.
        public abstract (int ExitCode, string Output) RunCommand(string command);

        public ProcessRunner(string runOnIP)
        {
            RunOnIP = runOnIP;
        }
    }
}
