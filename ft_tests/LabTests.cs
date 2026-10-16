namespace ft_tests
{
    /// <summary>
    /// The end-to-end tests find the lab's machines through <see cref="Lab"/>, from provisioning\settings.yml.
    /// These check that file still gives them everything they look up, without needing the lab itself.
    /// </summary>
    [TestClass]
    [TestCategory("Unit")]
    public class LabTests
    {
        [TestMethod]
        public void SettingsAreFound()
        {
            Assert.IsNotNull(Lab.FindSettings(), $"no provisioning\\settings.yml above the test binaries (or set {Lab.DirVariable})");
        }

        [TestMethod]
        public void EveryRoleTheTestsUseHasOneMachine()
        {
            var addresses = new[]
            {
                Lab.LinuxClient1Ip, Lab.LinuxServerIp, Lab.LinuxClient2Ip,
                Lab.WindowsClient1Ip, Lab.WindowsServerIp, Lab.WindowsClient2Ip,
                Lab.DevBoxIp, Lab.MacIp,
            };

            foreach (var address in addresses)
                Assert.IsTrue(System.Net.IPAddress.TryParse(address, out _), $"'{address}' is not an address");
            Assert.AreEqual(addresses.Length, addresses.Distinct().Count(), "two roles share an address: " + string.Join(", ", addresses));
            CollectionAssert.Contains(new[] { Lab.LinuxClient1Ip, Lab.LinuxServerIp, Lab.LinuxClient2Ip }, Lab.QemuHostIp,
                "the nested QEMU guest must run on one of the Linux test machines");
        }

        [TestMethod]
        public void TheMacRunsTwoEmulators()
        {
            Assert.AreEqual(2, Lab.EmulatorSerials.Count);
            Assert.AreNotEqual(Lab.EmulatorSerials[0], Lab.EmulatorSerials[1]);
            StringAssert.StartsWith(Lab.EmulatorSerials[0], "emulator-");
        }

        [TestMethod]
        public void LoginsAndServersAreSet()
        {
            foreach (var login in new[] { Lab.LinuxLogin, Lab.WindowsLogin, Lab.MacSmbLogin })
            {
                Assert.IsFalse(string.IsNullOrWhiteSpace(login.User));
                Assert.IsFalse(string.IsNullOrWhiteSpace(login.Password));
            }
            StringAssert.StartsWith(Lab.WebDavUrl, $"http://{Lab.LinuxServerIp}:");
            StringAssert.Contains(Lab.S3Args, $"--endpoint http://{Lab.LinuxServerIp}:");
            Assert.IsTrue(Lab.QemuGuestSshPort > 0);
            Assert.IsFalse(string.IsNullOrWhiteSpace(Lab.FtLogsFolder));
        }
    }
}
