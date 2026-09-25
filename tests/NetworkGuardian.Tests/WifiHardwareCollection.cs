using Xunit;

namespace NetworkGuardian.Tests;

/// <summary>
/// Serialises every test that talks to the real WLAN service.
/// </summary>
/// <remarks>
/// These tests write profiles, request scans and connect on the same adapters. Running two of them at once
/// is not "hardware flakiness", it is a collision: a parallel run produced
/// <c>ERROR_183 (Cannot create a file when that file already exists)</c> from <c>WlanSetProfile</c> with
/// <c>overwrite: true</c> while another test was mid-write. Sharing one collection keeps the unit tests
/// parallel (they do not touch hardware) and makes the hardware ones strictly sequential.
/// </remarks>
[CollectionDefinition(WifiHardwareCollection.Name, DisableParallelization = true)]
public sealed class WifiHardwareCollection
{
    public const string Name = "wifi-hardware";
}
