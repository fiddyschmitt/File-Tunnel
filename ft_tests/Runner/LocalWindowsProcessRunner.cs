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

        public LocalWindowsProcessRunner(string localExecutablePath) : base("127.0.0.1")
        {
            this.localExecutablePath = localExecutablePath;

            Stop();
        }

        public override void Stop()
        {
            var processName = Path.GetFileName(localExecutablePath);
            Process.Start("taskkill.exe", @$"/IM {processName} /F");
        }

        public override void Run(string args)
        {
            var psi = new ProcessStartInfo
            {
                FileName = localExecutablePath,
                Arguments = args,
                UseShellExecute = true
            };

            Debug.WriteLine($"\"{localExecutablePath}\" {args}");

            Process.Start(psi);
        }

        public override string GetFullCommand(string args)
        {
            var result = $"\"{localExecutablePath}\" {args}";
            return result;
        }
    }
}
