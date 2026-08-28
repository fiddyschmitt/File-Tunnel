using System.Text;

namespace ft_test_env.Config
{
    /// <summary>
    /// Strongly-typed configuration, bound from appsettings.json with secrets (Windows + SMB
    /// credentials) layered in from user-secrets. See appsettings.json for the defaults and
    /// the README/comments there for which keys belong in user-secrets.
    /// </summary>
    public class EnvConfig
    {
        public string VBoxManagePath { get; set; } = @"C:\Program Files\Oracle\VirtualBox\VBoxManage.exe";

        /// <summary>Folder holding the downloaded image, base.vdi and the generated seed ISOs.</summary>
        public string WorkingDir { get; set; } = @"C:\ft_test_env";

        public ImageConfig Image { get; set; } = new();
        public NetworkConfig Network { get; set; } = new();
        public LinuxConfig Linux { get; set; } = new();
        public List<NodeConfig> Nodes { get; set; } = [];
        public List<WindowsHostConfig> WindowsHosts { get; set; } = [];

        /// <summary>The manually-maintained Windows gold image the dedicated Windows nodes are linked-cloned from.</summary>
        public WindowsGoldConfig WindowsGold { get; set; } = new();

        /// <summary>The dedicated Windows test nodes (linked clones of the gold), differentiated by IP + hostname + role.</summary>
        public List<WindowsNodeConfig> WindowsNodes { get; set; } = [];

        /// <summary>The hand-built Windows SMB/RDP server VM (.84). NOT a clone: a same-SID clone cannot serve
        /// SMB/RDP to another clone (Windows 24H2+ SID checks — see FT_WIN_GOLD_IMAGE.md / memory
        /// windows-clone-sid-smb-rdp), so this VM is a fresh install with a DISTINCT SID. This tool only
        /// starts / health-checks / reboots it — it never builds or reconfigures it. It uses the same
        /// smith/villa2001 account as the gold (WindowsGold credentials).</summary>
        public WindowsServerConfig WindowsServer { get; set; } = new();

        /// <summary>Credentials keyed by name, supplied via user-secrets and referenced by Windows hosts.</summary>
        public Dictionary<string, Credential> Credentials { get; set; } = [];

        public string BaseVdiPath => Path.Combine(WorkingDir, "base.vdi");
        public string ImagePath => Path.Combine(WorkingDir, Image.FileName);
        public string SeedIsoPath(NodeConfig node) => Path.Combine(WorkingDir, $"{node.Name}-seed.iso");

        /// <summary>Persistent data disk for the QEMU-host node, holding the (large) nested-guest images
        /// off the tiny immutable root. Attached to SATA port 2 by VBoxManager; mounted at /var/lib/ftq.</summary>
        public string DataDiskPath(NodeConfig node) => Path.Combine(WorkingDir, $"{node.Name}-data.vdi");

        /// <summary>Nodes ordered so the server (.81) comes first — others mount its exports.</summary>
        public IEnumerable<NodeConfig> NodesServerFirst =>
            Nodes.OrderByDescending(n => n.IsServer).ThenBy(n => n.Name);

        /// <summary>Windows nodes ordered by clone name for stable, sequential bring-up (all boot at the
        /// gold SourceIp, so only one can be reconfigured at a time).</summary>
        public IEnumerable<WindowsNodeConfig> WindowsNodesOrdered => WindowsNodes.OrderBy(n => n.CloneName);

        public Credential? ResolveCredential(string? key) =>
            key != null && Credentials.TryGetValue(key, out var c) ? c : null;

        /// <summary>Reads mounts.sh and substitutes the SMB credential placeholders (__SMB_USER__ /
        /// __SMB_PASS__) with the 'smb' user-secret (Credentials:smb). Both the cloud-init seed and the
        /// orchestrator's re-mount render the script through this, so the real SMB password is never
        /// stored in the committed script. Missing secret -> empty creds (an anonymous/guest mount attempt).</summary>
        public byte[] RenderMountsScript(string mountsScriptPath)
        {
            var smb = ResolveCredential("smb");
            var text = File.ReadAllText(mountsScriptPath)
                .Replace("__SMB_USER__", smb?.Username ?? "")
                .Replace("__SMB_PASS__", smb?.Password ?? "");
            return Encoding.UTF8.GetBytes(text);
        }
    }

    public class ImageConfig
    {
        /// <summary>URL of the Debian "generic" cloud qcow2 (broader drivers than genericcloud for VirtualBox).</summary>
        public string Url { get; set; } = "";

        /// <summary>Expected SHA512 of the downloaded image (lowercase hex). Empty disables the check.
        /// Debian publishes SHA512SUMS alongside each image.</summary>
        public string Sha512 { get; set; } = "";

        /// <summary>Local filename for the downloaded image.</summary>
        public string FileName { get; set; } = "debian-generic-amd64.qcow2";
    }

    public class NetworkConfig
    {
        /// <summary>Host NIC name passed to VBoxManage --bridgeadapter1 (e.g. "Intel(R) Ethernet ...").</summary>
        public string BridgeAdapter { get; set; } = "";

        public string Gateway { get; set; } = "192.168.0.1";
        public string Dns { get; set; } = "8.8.8.8";
        public int PrefixLength { get; set; } = 24;
    }

    public class LinuxConfig
    {
        public string Username { get; set; } = "user";
        public string Password { get; set; } = "live";

        /// <summary>systemd units expected active on every node. Sourced from appsettings.json
        /// (kept empty here so the config binder replaces rather than appends to it).</summary>
        public List<string> Services { get; set; } = [];

        /// <summary>Mount points expected present on every node (findmnt).</summary>
        public List<string> ExpectedMounts { get; set; } = [];

        public int SshPort { get; set; } = 22;

        /// <summary>How long to wait for SSH to come up after starting a node.</summary>
        public int SshReadyTimeoutSeconds { get; set; } = 180;

        /// <summary>How long to wait for all services to become active (cloud-init runs apt on first boot).</summary>
        public int ServicesReadyTimeoutSeconds { get; set; } = 420;

        public int MemoryMb { get; set; } = 2048;
        public int Cpus { get; set; } = 2;

        // The QEMU-host node (QemuHost=true) runs a nested KVM guest, so it gets more RAM/CPU and a
        // dedicated data disk - the immutable 2.8 GB root cannot hold the guest images + libguestfs.
        public int QemuHostMemoryMb { get; set; } = 3072;
        public int QemuHostCpus { get; set; } = 4;
        public int QemuHostDataDiskMb { get; set; } = 15360;

        // The build host (BuildHost=true) cross-compiles the Android/Termux (linux-bionic-arm64) build with
        // NativeAOT. The .NET SDK + Android NDK + NuGet/build caches (several GB) live on a dedicated persistent
        // data disk (the immutable ~2.8 GB root can't hold them), and the ILC is RAM-hungry, so it gets more
        // RAM/CPU than a plain node. Provisioned by build_host_setup.sh (NOT the shared setup_debian.sh).
        public int BuildHostMemoryMb { get; set; } = 6144;
        public int BuildHostCpus { get; set; } = 4;
        public int BuildHostDataDiskMb { get; set; } = 25600;
    }

    public class NodeConfig
    {
        public string Name { get; set; } = "";       // VirtualBox VM name, e.g. ft-node-81
        public string Hostname { get; set; } = "";    // guest hostname
        public string Ip { get; set; } = "";          // static IP, e.g. 192.168.0.81
        public bool IsServer { get; set; }            // true for the NFS/SMB/FTP server (.81)
        public bool QemuHost { get; set; }            // true for the node running the nested QEMU guest (virtio-fs/9p)
        public bool BuildHost { get; set; }           // true for the NativeAOT cross-compile host (Android/Termux build - issue #45)
    }

    public class WindowsHostConfig
    {
        public string Name { get; set; } = "";
        public string Host { get; set; } = "";

        /// <summary>Key into EnvConfig.Credentials; null for the local host (no creds needed).</summary>
        public string? CredentialKey { get; set; }

        public List<WindowsCheck> Checks { get; set; } = [];
    }

    public enum WindowsCheckType
    {
        TcpPort,      // TCP Port is open (connect succeeds)
        UdpListener,  // a process is bound to the UDP Port — verified over SSH, since a UDP
                      // service (e.g. runremote) cannot be confirmed by a TCP probe
        SmbShare,     // Target UNC share is listable
        NetShare,     // local 'net share' exposes share named Target
        PathExists    // local path Target exists
    }

    public class WindowsCheck
    {
        public WindowsCheckType Type { get; set; }
        public int Port { get; set; }
        public string? Target { get; set; }
        public string? Description { get; set; }
    }

    public class Credential
    {
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
    }

    /// <summary>Which position a dedicated Windows node plays in a tunnel: client1 (side1), the SMB/RDP
    /// server, or client2 (side2). Drives the ft-specific per-node provisioning + which ft_tests runner
    /// it maps to.</summary>
    public enum WindowsRole { Client1, Server, Client2 }

    /// <summary>
    /// The Windows gold image (built + maintained by hand; see FT_WIN_GOLD_IMAGE.md). This tool
    /// linked-clones it but never modifies it. The gold has a baked-in static IP (<see cref="SourceIp"/>);
    /// every clone boots there first and is then moved to its own IP. Password comes from user-secrets
    /// ("WindowsGold:Password"). Set Enabled=false to run only the Linux nodes.
    /// </summary>
    public class WindowsGoldConfig
    {
        public bool Enabled { get; set; }

        public string GoldVmName { get; set; } = "ft-win-gold";

        /// <summary>Snapshot on the gold to linked-clone from (the pristine state; re-taken each bring-up).</summary>
        public string GoldSnapshot { get; set; } = "clean";

        /// <summary>The static IP baked into the gold — where every fresh clone boots before reconfiguration.</summary>
        public string SourceIp { get; set; } = "192.168.0.90";

        public string Username { get; set; } = "smith";

        /// <summary>SSH password — set via user-secrets ("WindowsGold:Password"), not committed.</summary>
        public string Password { get; set; } = "";

        public int SshPort { get; set; } = 22;
        public int SshReadyTimeoutSeconds { get; set; } = 240;

        /// <summary>How long to wait for a clone to reboot and come back on its new IP after reconfiguration.</summary>
        public int ReconfigTimeoutSeconds { get; set; } = 300;

        /// <summary>How long to wait for the gold to ACPI-shut-down before forcing it off.</summary>
        public int ShutdownTimeoutSeconds { get; set; } = 60;

        public int MemoryMb { get; set; } = 4096;
        public int Cpus { get; set; } = 2;

        // ft-specific volatile binaries deployed to each clone at bring-up, so they stay current with the
        // build (the gold bakes a working copy, but these override it each run). Baked bits (runremote
        // autostart, Client-for-NFS, Shared share, RDP, autologon) live in the gold — see FT_WIN_GOLD_IMAGE.md.

        /// <summary>ft.exe SCP'd to C:\Temp\ft\ft.exe on each clone.</summary>
        public string FtExeSource { get; set; } = @"R:\Temp\ft release\win-x64\ft.exe";

        /// <summary>RunRemote server self-contained win-x64 publish FOLDER, SCP'd to C:\Temp\runremote\.</summary>
        public string RunRemoteSource { get; set; } = @"C:\Users\Smith\Desktop\dev\cs\RunRemote\server\bin\Release\net8.0-windows\win-x64\publish";

        /// <summary>Folder served as the 'Shared' SMB share; re-asserted idempotently each bring-up.</summary>
        public string SharedSharePath { get; set; } = @"C:\Temp\ft\Shared";
    }

    public class WindowsNodeConfig
    {
        public string CloneName { get; set; } = "";   // VirtualBox VM name of the linked clone
        public string Ip { get; set; } = "";           // target static IP after reconfiguration
        public string Hostname { get; set; } = "";     // target computer name (renamed; requires a reboot)
        public WindowsRole Role { get; set; }          // client1 / server / client2
    }

    /// <summary>The hand-built Windows SMB/RDP server VM (.84, distinct SID). Not cloned or reconfigured by this
    /// tool — only started, health-checked and rebooted. Shares the gold's smith/villa2001 SSH account.</summary>
    public class WindowsServerConfig
    {
        public bool Enabled { get; set; }
        public string VmName { get; set; } = "ft-win-server";   // VirtualBox VM name
        public string Ip { get; set; } = "192.168.0.84";        // its fixed static IP (set during the manual build)
        public string SharedSharePath { get; set; } = @"C:\Temp\ft\Shared";
    }
}
