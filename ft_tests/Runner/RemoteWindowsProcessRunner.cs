using ft;
using Renci.SshNet;
using System;
using System.Diagnostics;

namespace ft_tests.Runner
{
    public class RemoteWindowsProcessRunner : ProcessRunner
    {
        readonly SshClient sshClient;
        private readonly string host;
        private readonly string? remoteExecutablePath;

        // ft's console output on the VM, kept by Stop() in localLogFile on the PC (when given): ft runs in the
        // logged-on session, where its window and output are otherwise gone by the next row.
        private const string RemoteLogFile = @"C:\Temp\ft\ft-run.log";
        private readonly string? localLogFile;
        private string? launchedArgs;                    // the ft launched since its log was last kept, if any

        public RemoteWindowsProcessRunner(string host, string username, string password, string? localExecutablePath = null,
                                          string? localLogFile = null) : base(host)
        {
            this.localLogFile = localLogFile;
            if (localExecutablePath != null)
            {
                var remoteFolder = "/C:/Temp/ft/";
                remoteExecutablePath = remoteFolder + Path.GetFileName(localExecutablePath);

                sshClient = new SshClient(host, username, password);
                sshClient.Connect();
                sshClient.ExecuteBounded(@$"mkdir ""{remoteFolder}""");


                Stop();

                var scpClient = new ScpClient(host, username, password);
                scpClient.Connect();
                scpClient.Upload(new FileInfo(localExecutablePath), remoteExecutablePath);

                this.host = host;

                remoteExecutablePath = Path.Combine(@"C:\Temp\ft", Path.GetFileName(localExecutablePath));
            }
        }

        // The runremote client that asks the node's runremote server (UDP 8888) to launch ft in its interactive
        // session. Wait for its ack (bounded) so a dropped datagram is retried and a dead runremote surfaces here.
        const string RunRemoteClient = @"C:\Users\Smith\Desktop\dev\cs\RunRemote\runremote\bin\Debug\net8.0\runremote.exe";

        private void InvokeRunRemote(string target, string trailing)
        {
            var rrArgs = $"{host}:8888 \"{target}\" {trailing}";
            Debug.WriteLine($"\"{target}\" {trailing}");
            var psi = new ProcessStartInfo(RunRemoteClient, rrArgs)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null) { Debug.WriteLine($"runremote: could not start client for {host}"); return; }
            if (!p.WaitForExit(15000)) { try { p.Kill(); } catch { } Debug.WriteLine($"runremote: client to {host} timed out"); return; }
            var outp = (p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd()).Trim();
            if (p.ExitCode != 0)
                Debug.WriteLine($"WARNING runremote on {host} did not launch (exit {p.ExitCode}): {outp}");
            else
                Debug.WriteLine($"runremote {host}: {outp}");
        }

        // True if the node's runremote server answers a PING - a fast liveness probe before relying on it.
        public bool RunRemoteAlive()
        {
            try
            {
                var psi = new ProcessStartInfo(RunRemoteClient, $"{host}:8888 PING")
                { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
                using var p = Process.Start(psi);
                if (p == null || !p.WaitForExit(6000)) { try { p?.Kill(); } catch { } return false; }
                return p.ExitCode == 0 && p.StandardOutput.ReadToEnd().Contains("PONG");
            }
            catch { return false; }
        }

        public override void Run(string args)
        {
            // Through cmd, so ft's output goes to a file - unless an argument holds a character cmd would act on.
            if (localLogFile is null || args.IndexOfAny(['&', '|', '<', '>', '^', '%', '"']) >= 0)
            {
                InvokeRunRemote(remoteExecutablePath ?? "", args);
                return;
            }
            InvokeRunRemote(@"C:\Windows\System32\cmd.exe", $"/c {remoteExecutablePath} {args} > {RemoteLogFile} 2>&1");
            launchedArgs = args;
        }

        public override string GetFullCommand(string args)
        {
            var result = $"\"{remoteExecutablePath}\" {args}";
            return result;
        }

        public override TimeSpan? Stop()
        {
            var processName = Path.GetFileName(remoteExecutablePath);
            // ExecuteBounded hard-caps the whole call INCLUDING channel-open. A tiring Windows node whose sshd
            // stops answering channel-open used to hang here forever (CommandTimeout does not cover Open()),
            // wedging the whole run - the exact 4.5h stall this replaced.
            sshClient.ExecuteBounded(@$"taskkill /IM {processName} /F", 15);
            KeepLog();
            return null;
        }

        private void KeepLog()
        {
            if (localLogFile is null || launchedArgs is null) return;
            var log = sshClient.ExecuteBounded($"Get-Content -Raw -LiteralPath '{RemoteLogFile}'", 60);
            File.AppendAllText(localLogFile, $"===== {DateTime.Now:yyyy-MM-dd HH:mm:ss} {host}: ft {launchedArgs}{Environment.NewLine}{log}{Environment.NewLine}");
            launchedArgs = null;
        }

        public override void DeleteFile(string path)
        {
            // In PowerShell, the VMs' SSH shell (provisioning\windows\provision.ps1); retrying while something
            // still holds the file open. (The cmd loop this replaced was a PowerShell parse error, so nothing was
            // ever deleted.)
            var p = path.Replace("'", "''");
            var cmd = $"$p = '{p}'; for ($i = 0; $i -lt 15 -and (Test-Path -LiteralPath $p); $i++) " +
                      "{ Remove-Item -LiteralPath $p -Force -ErrorAction SilentlyContinue; if (Test-Path -LiteralPath $p) { Start-Sleep -Seconds 1 } }";
            Debug.WriteLine(cmd);
            sshClient.ExecuteBounded(cmd, 20); // hard-bounded (covers channel-open); the loop can take 15 s, so cap it
        }

        public override void Run(string cmd, string args)
        {
            InvokeRunRemote(cmd, args);
            Thread.Sleep(5000);
        }

        // A console command (e.g. curl) runs fine over the existing SSH channel and its output is captured -
        // unlike ft's launch, which needs the interactive-session runremote path. Loopback (127.0.0.1) is
        // not session-isolated, so curl here can reach ft's SOCKS port regardless of which session ft runs in.
        public override (int ExitCode, string Output) RunCommand(string command)
        {
            if (sshClient == null) throw new InvalidOperationException("RunCommand requires the SSH client (constructed with a non-null executable path).");
            Debug.WriteLine(command);
            var (output, status, completed) = sshClient.ExecuteHardBounded(command);
            return completed ? (status, output) : (-1, "[ssh command timed out]");
        }
    }
}
