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
        private readonly string outputFilename;

        public LocalWindowsProcessRunner(string localExecutablePath, string outputFilename) : base("127.0.0.1")
        {
            this.localExecutablePath = localExecutablePath;
            this.outputFilename = outputFilename;

            Stop();
        }

        public override TimeSpan? Stop()
        {
            var processName = Path.GetFileName(localExecutablePath);
            Process.Start("taskkill.exe", @$"/IM {processName} /F");

            var result = process?.TotalProcessorTime;
            return result;
        }

        Process? process;

        public override void Run(string args)
        {
            var psi = new ProcessStartInfo
            {
                FileName = localExecutablePath,
                Arguments = args,
                UseShellExecute = false,  // must be false to redirect output
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            Debug.WriteLine($"\"{localExecutablePath}\" {args}");

            process = new Process { StartInfo = psi };

            // Subscribe to both output and error streams
            process.OutputDataReceived += (s, e) =>
            {
                if (e.Data != null)
                    File.AppendAllText(outputFilename, e.Data + Environment.NewLine);
            };
            process.ErrorDataReceived += (s, e) =>
            {
                if (e.Data != null)
                    File.AppendAllText(outputFilename, "[ERR] " + e.Data + Environment.NewLine);
            };

            process.Start();

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

        }

        public override string GetFullCommand(string args)
        {
            var result = $"\"{localExecutablePath}\" {args}";
            return result;
        }

        public override void DeleteFile(string path)
        {
            while (File.Exists(path))
            {
                try
                {
                    File.Delete(path);
                }
                catch { }
                Thread.Sleep(1000);
            }
        }

        public override void Run(string cmd, string args)
        {
            var psi = new ProcessStartInfo
            {
                //Verb = "runas",   //runs as admin, but prompts every time

                FileName = cmd,
                Arguments = args,
                UseShellExecute = true
            };

            Debug.WriteLine($"{cmd} {args}");

            var process = Process.Start(psi);
            process?.WaitForExit();
        }
    }
}
