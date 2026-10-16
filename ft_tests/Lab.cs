using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ft_tests
{
    /// <summary>A login the tests use on the lab's machines.</summary>
    public sealed class Login
    {
        public string User { get; set; } = "";
        public string Password { get; set; } = "";
    }

    /// <summary>
    /// The lab the end-to-end tests run against, as provisioning\settings.yml defines it - the file the
    /// lab is built from (see provisioning\README.md): each machine's address by its OS and role, the
    /// logins, and the servers' settings. The tests look for provisioning\settings.yml in each folder
    /// above their binaries; <see cref="DirVariable"/> names the folder instead.
    /// </summary>
    public static class Lab
    {
        public const string DirVariable = "FT_LAB_DIR";

        private static readonly Lazy<SettingsYml> settings = new(Load);
        private static SettingsYml Settings => settings.Value;

        // The VMs, by role: client1 and client2 are the two ends of a tunnel, server the file server.
        public static string LinuxClient1Ip => Ip("linux", "client1");
        public static string LinuxServerIp => Ip("linux", "server");
        public static string LinuxClient2Ip => Ip("linux", "client2");
        public static string WindowsClient1Ip => Ip("windows", "client1");
        public static string WindowsServerIp => Ip("windows", "server");
        public static string WindowsClient2Ip => Ip("windows", "client2");

        /// <summary>The Linux VM that runs the nested QEMU guest (virtio-fs and virtio-9p).</summary>
        public static string QemuHostIp => Settings.Machines.Single(m => m.QemuHost).Ip;
        /// <summary>Where the nested guest's SSH is forwarded to, on <see cref="QemuHostIp"/>.</summary>
        public static int QemuGuestSshPort => Settings.Linux.QemuGuest.SshPort;

        /// <summary>This PC.</summary>
        public static string DevBoxIp => Settings.DevBox.Ip;
        /// <summary>The folder on this PC where ft's output on the Linux VMs is written
        /// (<see cref="LinuxFtLogsDir"/> in the VMs).</summary>
        public static string FtLogsFolder => Settings.DevBox.SharedFolders["ft_logs"];
        /// <summary>The same folder, as the Linux VMs see it.</summary>
        public static string LinuxFtLogsDir => $"/media/vboxsf/{DevBoxIp}/ft_logs";

        public static string MacIp => Settings.Mac.Host;
        public static string MacUser => Settings.Mac.User;
        public static string MacHome => $"/Users/{MacUser}";
        /// <summary>The adb serials of the two Android emulators on the Mac: client1's, then client2's.</summary>
        public static IReadOnlyList<string> EmulatorSerials => Settings.Mac.Emulators.Ports.Select(p => $"emulator-{p}").ToList();

        public static Login LinuxLogin => Settings.Accounts.Linux;
        public static Login WindowsLogin => Settings.Accounts.Windows;
        public static Login MacSmbLogin => Settings.Accounts.MacSmb;
        /// <summary>The lab's SSH key (the tests log in to the Mac with it).</summary>
        public static string SshKey => ExpandHome(Settings.SshKey);

        public static string WebDavUrl => $"http://{LinuxServerIp}:{Settings.Linux.Webdav.Port}{Settings.Linux.Webdav.Path}";
        public static string S3Args
        {
            get
            {
                var s3 = Settings.Linux.S3;
                return $"--s3 --bucket {s3.Bucket} --endpoint http://{LinuxServerIp}:{s3.Port} --access-key {s3.AccessKey} --secret-key {s3.SecretKey}";
            }
        }

        private static string Ip(string os, string role) =>
            Settings.Machines.SingleOrDefault(m => m.Os == os && m.Role == role)?.Ip
            ?? throw new InvalidOperationException($"settings.yml has no {os} machine with role {role}");

        /// <summary>The path of the lab's settings.yml, or null when there's none to be found.</summary>
        public static string? FindSettings()
        {
            var dir = Environment.GetEnvironmentVariable(DirVariable);
            if (!string.IsNullOrWhiteSpace(dir))
                return Path.Combine(dir, "settings.yml");
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            {
                var candidate = Path.Combine(d.FullName, "provisioning", "settings.yml");
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }

        private static SettingsYml Load()
        {
            var path = FindSettings()
                ?? throw new FileNotFoundException($"No provisioning\\settings.yml above {AppContext.BaseDirectory}; set {DirVariable} to the folder that has it.");
            return new DeserializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build()
                .Deserialize<SettingsYml>(File.ReadAllText(path));
        }

        private static string ExpandHome(string path) =>
            path == "~" || path.StartsWith("~/") || path.StartsWith(@"~\")
                ? Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + path[1..])
                : path;

        // The parts of settings.yml the tests read; the rest is for Vagrant and the provisioners.
        private sealed class SettingsYml
        {
            public string SshKey { get; set; } = "";
            public DevBoxYml DevBox { get; set; } = new();
            public AccountsYml Accounts { get; set; } = new();
            public List<MachineYml> Machines { get; set; } = [];
            public LinuxYml Linux { get; set; } = new();
            public MacYml Mac { get; set; } = new();
        }

        private sealed class DevBoxYml
        {
            public string Ip { get; set; } = "";
            public Dictionary<string, string> SharedFolders { get; set; } = [];
        }

        private sealed class AccountsYml
        {
            public Login Linux { get; set; } = new();
            public Login Windows { get; set; } = new();
            public Login MacSmb { get; set; } = new();
        }

        private sealed class MachineYml
        {
            public string Name { get; set; } = "";
            public string Os { get; set; } = "";
            public string Role { get; set; } = "";
            public string Ip { get; set; } = "";
            public bool QemuHost { get; set; }
        }

        private sealed class LinuxYml
        {
            public S3Yml S3 { get; set; } = new();
            public WebDavYml Webdav { get; set; } = new();
            public QemuGuestYml QemuGuest { get; set; } = new();
        }

        private sealed class S3Yml
        {
            public int Port { get; set; }
            public string AccessKey { get; set; } = "";
            public string SecretKey { get; set; } = "";
            public string Bucket { get; set; } = "";
        }

        private sealed class WebDavYml
        {
            public int Port { get; set; }
            public string Path { get; set; } = "";
        }

        private sealed class QemuGuestYml
        {
            public int SshPort { get; set; }
        }

        private sealed class MacYml
        {
            public string Name { get; set; } = "";
            public string Host { get; set; } = "";
            public string User { get; set; } = "";
            public EmulatorsYml Emulators { get; set; } = new();
        }

        private sealed class EmulatorsYml
        {
            public List<int> Ports { get; set; } = [];
        }
    }
}
