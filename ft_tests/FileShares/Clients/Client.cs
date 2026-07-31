using ft_tests.Runner;

namespace ft_tests.FileShares.Clients;

public class Client
{
    public Client(OS os, ProcessRunner runner, string args)
    {
        OS = os;
        Runner = runner;
        Args = args;
    }

    public virtual void Restart()
    {

    }

    public OS OS { get; }
    public ProcessRunner Runner { get; }
    public string Args { get; }
}