using NetworkGuardian.Windows.Native;
using Xunit;

namespace NetworkGuardian.Tests;

/// <summary>
/// Locks the hand written device property selectors to the values in the Windows SDK headers.
/// The CM_DRP_* and SPDRP_* sets differ (SPDRP_DRIVER is 0x09, CM_DRP_DRIVER is 0x0A), and using one
/// where the other is expected silently returns the wrong property - which is how the PnP/WLAN
/// correlation broke before this test existed.
/// </summary>
public sealed class DevicePropertySelectorTests
{
    [Theory]
    [InlineData("CM_DRP_DEVICEDESC", CfgMgr32Native.CM_DRP_DEVICEDESC, 0x01)]
    [InlineData("CM_DRP_HARDWAREID", CfgMgr32Native.CM_DRP_HARDWAREID, 0x02)]
    [InlineData("CM_DRP_COMPATIBLEIDS", CfgMgr32Native.CM_DRP_COMPATIBLEIDS, 0x03)]
    [InlineData("CM_DRP_SERVICE", CfgMgr32Native.CM_DRP_SERVICE, 0x05)]
    [InlineData("CM_DRP_CLASS", CfgMgr32Native.CM_DRP_CLASS, 0x08)]
    [InlineData("CM_DRP_CLASSGUID", CfgMgr32Native.CM_DRP_CLASSGUID, 0x09)]
    [InlineData("CM_DRP_DRIVER", CfgMgr32Native.CM_DRP_DRIVER, 0x0A)]
    [InlineData("CM_DRP_CONFIGFLAGS", CfgMgr32Native.CM_DRP_CONFIGFLAGS, 0x0B)]
    [InlineData("CM_DRP_MFG", CfgMgr32Native.CM_DRP_MFG, 0x0C)]
    [InlineData("CM_DRP_FRIENDLYNAME", CfgMgr32Native.CM_DRP_FRIENDLYNAME, 0x0D)]
    [InlineData("CM_DRP_LOCATION_INFORMATION", CfgMgr32Native.CM_DRP_LOCATION_INFORMATION, 0x0E)]
    [InlineData("CM_DRP_CAPABILITIES", CfgMgr32Native.CM_DRP_CAPABILITIES, 0x10)]
    [InlineData("CM_DRP_BUSTYPEGUID", CfgMgr32Native.CM_DRP_BUSTYPEGUID, 0x14)]
    [InlineData("CM_DRP_BUSNUMBER", CfgMgr32Native.CM_DRP_BUSNUMBER, 0x16)]
    [InlineData("CM_DRP_ENUMERATOR_NAME", CfgMgr32Native.CM_DRP_ENUMERATOR_NAME, 0x17)]
    [InlineData("CM_DRP_ADDRESS", CfgMgr32Native.CM_DRP_ADDRESS, 0x1D)]
    [InlineData("CM_DRP_INSTALL_STATE", CfgMgr32Native.CM_DRP_INSTALL_STATE, 0x23)]
    [InlineData("CM_DRP_LOCATION_PATHS", CfgMgr32Native.CM_DRP_LOCATION_PATHS, 0x24)]
    public void Selector_MatchesTheSdkHeader(string name, uint actual, uint expected)
    {
        Assert.Equal(expected, actual);
        Assert.False(string.IsNullOrWhiteSpace(name));
    }
}
