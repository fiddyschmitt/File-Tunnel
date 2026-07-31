using ft_tests.Runner;

namespace ft_tests.FileShares.Servers;

public class NfsServer : Server
{
    private readonly ProcessRunner processRunner;

    public NfsServer(ProcessRunner processRunner) : base(OS.Linux, FileShareType.NFS)
    {
        this.processRunner = processRunner;
    }

    public override void Restart()
    {
        if (OS == OS.Linux) processRunner.Run("systemctl", "restart nfs-server");
    }
}