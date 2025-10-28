using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ft.Utilities
{
    public static class SystemUtils
    {
        public static bool IsRunningInCitrix()
        {
            var clientName = Environment.GetEnvironmentVariable("CLIENTNAME") ?? "";
            if (!string.IsNullOrEmpty(clientName) && clientName.StartsWith("CTX", StringComparison.OrdinalIgnoreCase))
                return true;

            if (Environment.GetEnvironmentVariable("CITRIX_METAINFO") != null ||
                Environment.GetEnvironmentVariable("CTXSMACHINE") != null)
                return true;

            if (Process.GetProcessesByName("wfica32").Length > 0 ||
                Process.GetProcessesByName("wfcrun32").Length > 0)
                return true;

            return false;
        }
    }
}
