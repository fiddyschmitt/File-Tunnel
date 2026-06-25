using System.Text;
using DiscUtils.Iso9660;
using ft_test_env.Config;

namespace ft_test_env.Cloud
{
    /// <summary>
    /// Builds the cloud-init NoCloud "cidata" seed ISO for a node: static networking, the
    /// 'user' account with password login + NOPASSWD sudo, the test packages, and the
    /// canonical setup_debian.sh embedded and run on first boot.
    /// </summary>
    public class CloudInitSeed
    {
        private readonly EnvConfig config;
        private readonly string setupScriptPath;
        private readonly string mountsScriptPath;

        public CloudInitSeed(EnvConfig config)
        {
            this.config = config;
            // Copied next to the executable via the csproj (Cloud\*.sh).
            setupScriptPath = Path.Combine(AppContext.BaseDirectory, "Cloud", "setup_debian.sh");
            mountsScriptPath = Path.Combine(AppContext.BaseDirectory, "Cloud", "mounts.sh");
        }

        public void BuildSeedIso(NodeConfig node, string isoPath)
        {
            var builder = new CDBuilder
            {
                UseJoliet = true,
                VolumeIdentifier = "cidata"   // cloud-init NoCloud matches this label (case-insensitive)
            };

            builder.AddFile("meta-data", Encoding.ASCII.GetBytes(RenderMetaData(node)));
            builder.AddFile("user-data", Encoding.ASCII.GetBytes(RenderUserData(node)));
            builder.AddFile("network-config", Encoding.ASCII.GetBytes(RenderNetworkConfig(node)));

            builder.Build(isoPath);
        }

        private static string RenderMetaData(NodeConfig node) =>
            $"instance-id: {node.Hostname}\n" +
            $"local-hostname: {node.Hostname}\n";

        private string RenderUserData(NodeConfig node)
        {
            if (!File.Exists(setupScriptPath))
            {
                throw new FileNotFoundException($"Provisioning script not found: {setupScriptPath}");
            }
            if (!File.Exists(mountsScriptPath))
            {
                throw new FileNotFoundException($"Mounts script not found: {mountsScriptPath}");
            }

            var scriptB64 = Convert.ToBase64String(File.ReadAllBytes(setupScriptPath));
            // mounts.sh carries the SMB credential placeholders, filled from the 'smb' user-secret here.
            var mountsB64 = Convert.ToBase64String(config.RenderMountsScript(mountsScriptPath));

            var sb = new StringBuilder();
            sb.Append("#cloud-config\n");
            sb.Append($"hostname: {node.Hostname}\n");
            sb.Append("preserve_hostname: false\n");
            sb.Append("users:\n");
            sb.Append($"  - name: {config.Linux.Username}\n");
            sb.Append($"    plain_text_passwd: {config.Linux.Password}\n");
            sb.Append("    lock_passwd: false\n");
            sb.Append("    shell: /bin/bash\n");
            sb.Append("    sudo: ALL=(ALL) NOPASSWD:ALL\n");
            sb.Append("    groups: [sudo]\n");
            sb.Append("ssh_pwauth: true\n");
            sb.Append("package_update: true\n");
            sb.Append("packages:\n");
            sb.Append("  - nfs-kernel-server\n");
            sb.Append("  - nfs-common\n");
            sb.Append("  - samba\n");
            sb.Append("  - samba-client\n");
            sb.Append("  - cifs-utils\n");
            sb.Append("  - vsftpd\n");
            sb.Append("  - tinyproxy\n");
            sb.Append("write_files:\n");
            sb.Append("  - path: /opt/ft/setup_debian.sh\n");
            sb.Append("    permissions: '0755'\n");
            sb.Append("    encoding: b64\n");
            sb.Append($"    content: {scriptB64}\n");
            sb.Append("  - path: /opt/ft/mounts.sh\n");
            sb.Append("    permissions: '0755'\n");
            sb.Append("    encoding: b64\n");
            sb.Append($"    content: {mountsB64}\n");
            sb.Append("runcmd:\n");
            sb.Append("  - [ bash, /opt/ft/setup_debian.sh ]\n");
            return sb.ToString();
        }

        private string RenderNetworkConfig(NodeConfig node)
        {
            var sb = new StringBuilder();
            sb.Append("version: 2\n");
            sb.Append("ethernets:\n");
            sb.Append("  primary:\n");
            sb.Append("    match:\n");
            sb.Append("      name: \"en*\"\n");          // matches enp0s3 etc. under VirtualBox
            sb.Append($"    addresses: [{node.Ip}/{config.Network.PrefixLength}]\n");
            sb.Append("    routes:\n");
            sb.Append("      - to: default\n");
            sb.Append($"        via: {config.Network.Gateway}\n");
            sb.Append("    nameservers:\n");
            sb.Append($"      addresses: [{config.Network.Dns}]\n");
            return sb.ToString();
        }
    }
}
