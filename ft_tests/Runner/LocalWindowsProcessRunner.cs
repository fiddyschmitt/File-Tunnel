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

            var processName = Path.GetFileName(localExecutablePath);
            Kill(processName);
        }

        public override void Kill(string process)
        {
            Process.Start("taskkill.exe", @$"/IM {process} /F");
        }

        public override void Run(string args)
        {
            var processName = Path.GetFileName(localExecutablePath);
            Kill(processName);

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
