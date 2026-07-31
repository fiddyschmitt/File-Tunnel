using ft_tests.Runner;

namespace ft_tests.FileShares.Servers;

public class SmbServer : Server
{
    private readonly ProcessRunner processRunner;

    public SmbServer(OS OS, ProcessRunner processRunner) : base(OS, FileShareType.SMB)
    {
        this.processRunner = processRunner;
    }

    public override void Restart()
    {
        if (OS == OS.Linux) processRunner.Run("systemctl", "restart smbd");
        if (OS == OS.Windows) processRunner.Run(@"cmd.exe", "/c net stop lanmanserver && net start lanmanserver");
    }
}