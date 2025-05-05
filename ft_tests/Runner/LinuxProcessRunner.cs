using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ft_tests.Runner
{
    public class LinuxProcessRunner : ProcessRunner
    {
        public LinuxProcessRunner(string host, string username, string password, string localExecutablePath, string remoteExecutablePath)
        {

        }

        public override void Kill(string process)
        {
            throw new NotImplementedException();
        }

        public override void Run(string args)
        {

        }
    }
}
