using NetworkGuardian.Core.Models;
using NetworkGuardian.Core.Policies;
using Xunit;

namespace NetworkGuardian.Tests;

/// <summary>
/// The most safety critical rules of the product: only real physical adapters may ever be managed.
/// </summary>
public sealed class PhysicalDeviceClassifierTests
{
    private readonly NetworkDeviceClassifier _classifier = new();

    [Fact]
    public void RealPciWifiAdapter_IsAccepted()
    {
        var record = TestData.Pnp(
            @"PCI\VEN_8086&DEV_51F0&SUBSYS_00948086&REV_01\3&11583659&0&A0",
            physicalMediaType: 9,
            friendlyName: "Intel(R) Wi-Fi 6E AX211 160MHz");

        var result = _classifier.Classify(record);

        Assert.True(result.IsPhysical);
        Assert.Equal(DeviceCategory.PhysicalWifi, result.Category);
    }

    [Fact]
    public void UsbWifiAdapter_IsAccepted()
    {
        var record = TestData.Pnp(
            @"USB\VID_0BDA&PID_C811\1234567890",
            enumerator: "USB",
            service: "rtwlanu",
            physicalMediaType: 9,
            friendlyName: "Realtek 8811CU Wireless LAN 802.11ac USB NIC",
            hardwareIds: new[] { @"USB\VID_0BDA&PID_C811&REV_0200" });

        var result = _classifier.Classify(record);

        Assert.True(result.IsPhysical);
        Assert.Equal(DeviceCategory.PhysicalWifi, result.Category);
    }

    [Fact]
    public void WlanServiceCorrelation_IsTreatedAsProofOfPhysicalWifi()
    {
        var guid = TestData.AdapterA;
        var record = TestData.Pnp(
            @"PCI\VEN_8086&DEV_A0F0\3&11583659&0&A0",
            physicalMediaType: null,
            friendlyName: "Wireless Adapter",
            netCfgInstanceId: guid.ToString("B"));

        var result = _classifier.Classify(record, new[] { guid });

        Assert.True(result.IsPhysical);
        Assert.Equal(DeviceCategory.PhysicalWifi, result.Category);
        Assert.Equal("wlansvc-correlation", result.Rule);
    }

    [Fact]
    public void RealEthernetAdapter_IsAccepted()
    {
        var record = TestData.Pnp(
            @"PCI\VEN_8086&DEV_15F3&SUBSYS_00008086&REV_01\3&11583659&0&B0",
            service: "e1dexpress",
            physicalMediaType: 14,
            friendlyName: "Intel(R) Ethernet Controller I225-V",
            hardwareIds: new[] { @"PCI\VEN_8086&DEV_15F3" });

        var result = _classifier.Classify(record);

        Assert.True(result.IsPhysical);
        Assert.Equal(DeviceCategory.PhysicalEthernet, result.Category);
    }

    [Theory]
    // Microsoft Wi-Fi Direct / hosted network virtual adapters report media type 9 as well, so they
    // are the most dangerous false positive.
    [InlineData(@"{5d624f94-8850-40c3-a3fa-a4fd2080baf3}\vwifimp_wfd0", "vwifimp_wfd0", "vwifibus")]
    [InlineData(@"{5d624f94-8850-40c3-a3fa-a4fd2080baf3}\vwifimp_hns0", "vwifimp_hns0", "vwifibus")]
    public void MicrosoftVirtualWifi_IsRejected(string instanceId, string hardwareId, string service)
    {
        var record = TestData.Pnp(
            instanceId,
            enumerator: "PCI",
            service: service,
            physicalMediaType: 9,
            friendlyName: "Microsoft Wi-Fi Direct Virtual Adapter",
            hardwareIds: new[] { hardwareId });

        var result = _classifier.Classify(record);

        Assert.False(result.IsPhysical);
        Assert.Equal(DeviceCategory.Virtual, result.Category);
    }

    [Theory]
    [InlineData(@"ROOT\NET\0000", "ROOT", "Microsoft KM-TEST Loopback Adapter", "msloopback")]
    [InlineData(@"ROOT\KDNIC\0000", "ROOT", "Microsoft Kernel Debug Network Adapter", "kdnic")]
    [InlineData(@"ROOT\NET\0002", "ROOT", "TAP-Windows Adapter V9", "tap0901")]
    [InlineData(@"ROOT\WIREGUARD\0000", "ROOT", "WireGuard Tunnel", "WireGuard")]
    [InlineData(@"SWD\WINTUN\0000", "SWD", "Wintun Userspace Tunnel", "wintun")]
    [InlineData(@"SWD\NVVPN\0000", "SWD", "Fortinet SSL VPN Virtual Ethernet Adapter", "fortinet")]
    public void SoftwareEnumeratedAdapters_AreRejected(string instanceId, string enumerator, string name, string service)
    {
        var record = TestData.Pnp(
            instanceId,
            enumerator: enumerator,
            service: service,
            physicalMediaType: 14,
            friendlyName: name,
            hardwareIds: new[] { instanceId });

        var result = _classifier.Classify(record);

        Assert.False(result.IsPhysical);
        Assert.NotEqual(DeviceCategory.PhysicalEthernet, result.Category);
    }

    [Theory]
    [InlineData(@"VMBUS\{5C9B0DF5-0000-0000-0000-000000000000}\{ABCD}", "VMBUS", "netvsc", "Hyper-V Virtual Ethernet Adapter")]
    [InlineData(@"PCI\VEN_15AD&DEV_07B0&SUBSYS_07B015AD&REV_01\4&1F4F0C5F&0&00E0", "PCI", "vmxnet3", "VMware VMXNET3 Ethernet Adapter")]
    [InlineData(@"PCI\VEN_8086&DEV_100E&SUBSYS_001E8086&REV_02\3&267A616A&0&18", "PCI", "netvsc", "VirtualBox Host-Only Ethernet Adapter")]
    [InlineData(@"ROOT\VBOXNETADP\0000", "ROOT", "vboxnetadp", "VirtualBox Host-Only Network")]
    public void HypervisorAdapters_AreRejected(string instanceId, string enumerator, string service, string name)
    {
        var record = TestData.Pnp(
            instanceId,
            enumerator: enumerator,
            service: service,
            physicalMediaType: 14,
            friendlyName: name,
            hardwareIds: new[] { instanceId });

        var result = _classifier.Classify(record);

        Assert.False(result.IsPhysical);
        Assert.Equal(DeviceCategory.Virtual, result.Category);
    }

    [Fact]
    public void BluetoothPanAdapter_IsRejected()
    {
        var record = TestData.Pnp(
            @"BTHENUM\{00001116-0000-1000-8000-00805F9B34FB}_LOCALMFG&0002\7&2AF5E3A4&0&001122334455_C00000000",
            enumerator: "BTHENUM",
            service: "BthPan",
            physicalMediaType: null,
            friendlyName: "Bluetooth Device (Personal Area Network)",
            hardwareIds: new[] { @"BTHENUM\{00001116-0000-1000-8000-00805F9B34FB}" });

        var result = _classifier.Classify(record);

        Assert.False(result.IsPhysical);
        Assert.Equal(DeviceCategory.Other, result.Category);
    }

    [Fact]
    public void DeviceWithoutLanMediaType_IsNotManaged()
    {
        var record = TestData.Pnp(
            @"PCI\VEN_8086&DEV_1234\3&11583659&0&C8",
            service: "someotherdriver",
            physicalMediaType: 0,
            friendlyName: "Some Unknown Network Device",
            hardwareIds: new[] { @"PCI\VEN_8086&DEV_1234" });

        var result = _classifier.Classify(record);

        Assert.False(result.IsPhysical);
        Assert.Equal("no-lan-media-type", result.Rule);
    }

    [Fact]
    public void PciDeviceOnVirtualVendor_IsRejected()
    {
        var record = TestData.Pnp(
            @"PCI\VEN_8086&DEV_100E\3&11583659&0&18",
            service: "e1i65x64",
            physicalMediaType: 14,
            friendlyName: "Ethernet Adapter",
            hardwareIds: new[] { @"PCI\VEN_15AD&DEV_07B0" });

        var result = _classifier.Classify(record);

        Assert.False(result.IsPhysical);
        Assert.Equal("virtual-pci-vendor", result.Rule);
    }

    [Fact]
    public void DenyListPattern_IsHonoured()
    {
        var record = TestData.Pnp(
            @"PCI\VEN_8086&DEV_51F0\3&11583659&0&A0",
            friendlyName: "Intel(R) Wi-Fi 6E AX211 160MHz");

        var result = _classifier.Classify(record, null, new[] { @"PCI\VEN_8086&DEV_51F0*" });

        Assert.False(result.IsPhysical);
        Assert.Equal("deny-list", result.Rule);
    }

    [Fact]
    public void AmbiguousDeviceWithoutPhysicalBus_IsNotManaged()
    {
        // A device living on an unknown enumerator with no WLAN correlation must never be touched.
        var record = TestData.Pnp(
            @"SOMEBUS\DEVICE\0000",
            enumerator: "SOMEBUS",
            service: "unknown",
            physicalMediaType: 9,
            friendlyName: "Wireless Adapter",
            hardwareIds: new[] { @"SOMEBUS\DEVICE" });

        var result = _classifier.Classify(record);

        Assert.False(result.IsPhysical);
        Assert.Equal("not-on-physical-bus", result.Rule);
    }
}
