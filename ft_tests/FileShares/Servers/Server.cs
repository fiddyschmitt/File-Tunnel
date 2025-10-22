using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ft_tests.FileShares.Servers
{
    public class Server
    {
        public Server(OS OS, FileShareType fileShareType)
        {
            this.OS = OS;
            FileShareType = fileShareType;
        }

        public OS OS { get; }
        public FileShareType FileShareType { get; }

        public virtual void Restart()
        {

        }
    }
}
