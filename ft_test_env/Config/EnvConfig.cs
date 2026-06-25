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
    }

    public class NodeConfig
    {
        public string Name { get; set; } = "";       // VirtualBox VM name, e.g. ft-node-81
        public string Hostname { get; set; } = "";    // guest hostname
        public string Ip { get; set; } = "";          // static IP, e.g. 192.168.0.81
        public bool IsServer { get; set; }            // true for the NFS/SMB/FTP server (.81)
        public bool QemuHost { get; set; }            // true for the node running the nested QEMU guest (virtio-fs/9p)
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
}
