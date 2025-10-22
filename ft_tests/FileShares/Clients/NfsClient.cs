using ft_tests.Runner;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ft_tests.FileShares.Clients
{
    public class NfsClient : Client
    {
        private readonly ProcessRunner runner;

        public NfsClient(OS os, ProcessRunner runner, string args) : base(os, runner, args)
        {
            this.runner = runner;
        }

        public override void Restart()
        {
            //could also use:
            //nfsadmin client stop
            //nfsadmin client start
            //But it also requires admin
            //if (OS == OS.Windows) runner.Run(@"cmd.exe", "/c net stop nfsclnt && net start nfsclnt");
            //Thread.Sleep(10000);

            if (OS == OS.Windows)
            {
                runner.Run("net.exe", "use * /delete /yes");

                runner.Run("umount.exe", "X:");

                runner.Run("mount.exe", "192.168.0.81:/mnt/tmpfs X:");

                //This causes NFS Windows-Linux-Windows to not work
                //runner.Run("mount.exe", "-o nolock,noac,nfsvers=4 192.168.0.81:/mnt/tmpfs X:");
            }

            if (OS == OS.Linux)
            {
                runner.Run("umount", "/media/nfs/192.168.0.81/tmpfs");
                runner.Run("mount", "-t nfs 192.168.0.81:/mnt/tmpfs /media/nfs/192.168.0.81/tmpfs");
            }
        }
    }
}
