using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ft_tests.Runner
{
    public class LocalWindowsProcessRunner : ProcessRunner
    {
        private readonly string localExecutablePath;

        public LocalWindowsProcessRunner(string localExecutablePath)
        {
            this.localExecutablePath = localExecutablePath;
        }

        public override void Stop()
        {
            var processName = Path.GetFileName(localExecutablePath);
            Process.Start("taskkill.exe", @$"/IM {processName} /F");
        }

        public override void Run(string args)
        {
            Stop();

            var psi = new ProcessStartInfo
            {
                FileName = localExecutablePath,
                Arguments = args,
                UseShellExecute = true
            };

            Process.Start(psi);
        }
    }
}
