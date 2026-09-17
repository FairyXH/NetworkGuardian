using NetworkGuardian.Core.Models;

namespace NetworkGuardian.Windows.Wlan;

/// <summary>Maps Native Wi-Fi / 802.11 numeric values onto the project's domain enums.</summary>
public static class WifiMapping
{
    // DOT11_AUTH_ALGORITHM
    private const uint Dot11AuthOpen = 1;
    private const uint Dot11AuthSharedKey = 2;
    private const uint Dot11AuthWpa = 3;
    private const uint Dot11AuthWpaPsk = 4;
    private const uint Dot11AuthWpaNone = 5;
    private const uint Dot11AuthRsna = 6;
    private const uint Dot11AuthRsnaPsk = 7;
    private const uint Dot11AuthWpa3 = 8;
    private const uint Dot11AuthWpa3Sae = 9;
    private const uint Dot11AuthWpa3Ent = 10;
    private const uint Dot11AuthOwe = 11;
    private const uint Dot11AuthWpa3Ent192 = 12;

    // DOT11_CIPHER_ALGORITHM
    private const uint Dot11CipherNone = 1;
    private const uint Dot11CipherWep40 = 2;
    private const uint Dot11CipherTkip = 3;
    private const uint Dot11CipherCcmp = 4;
    private const uint Dot11CipherWep104 = 5;
    private const uint Dot11CipherBip = 6;
    private const uint Dot11CipherGcmp = 8;
    private const uint Dot11CipherGcmp256 = 9;
    private const uint Dot11CipherCcmp256 = 10;
    private const uint Dot11CipherBipGmac256 = 11;

    public static WifiSecurity MapSecurity(bool securityEnabled, uint authAlgorithm, uint cipherAlgorithm)
    {
        if (!securityEnabled)
        {
            return WifiSecurity.Open;
        }

        return authAlgorithm switch
        {
            Dot11AuthOpen when cipherAlgorithm == Dot11CipherNone => WifiSecurity.Open,
            Dot11AuthOpen => WifiSecurity.Open,
            Dot11AuthOwe => WifiSecurity.EnhancedOpen,
            Dot11AuthSharedKey => WifiSecurity.Wep,
            Dot11AuthWpa => WifiSecurity.WpaEnterprise,
            Dot11AuthWpaPsk => WifiSecurity.WpaPersonal,
            Dot11AuthWpaNone => WifiSecurity.Open,
            Dot11AuthRsna => WifiSecurity.Wpa2Enterprise,
            Dot11AuthRsnaPsk => WifiSecurity.Wpa2Personal,
            Dot11AuthWpa3 => WifiSecurity.Wpa3Personal,
            Dot11AuthWpa3Sae => WifiSecurity.Wpa3Personal,
            Dot11AuthWpa3Ent or Dot11AuthWpa3Ent192 => WifiSecurity.Wpa3Enterprise,
            _ => cipherAlgorithm switch
            {
                Dot11CipherWep40 or Dot11CipherWep104 => WifiSecurity.Wep,
                Dot11CipherTkip => WifiSecurity.WpaPersonal,
                Dot11CipherCcmp or Dot11CipherBipGmac256 or Dot11CipherCcmp256 => WifiSecurity.Wpa2Personal,
                Dot11CipherGcmp or Dot11CipherGcmp256 => WifiSecurity.Wpa3Personal,
                _ => WifiSecurity.Unknown,
            },
        };
    }

    public static WifiBssType MapBssType(uint bssType) => bssType switch
    {
        1 => WifiBssType.Infrastructure,
        2 => WifiBssType.Independent,
        3 => WifiBssType.Any,
        _ => WifiBssType.Unknown,
    };

    public static WifiConnectionState MapInterfaceState(uint state) => state switch
    {
        0 => WifiConnectionState.NotReady,
        1 => WifiConnectionState.Connected,
        2 => WifiConnectionState.AdHocFormed,
        3 => WifiConnectionState.Disconnecting,
        4 => WifiConnectionState.Disconnected,
        5 => WifiConnectionState.Associating,
        6 => WifiConnectionState.Associating,
        7 => WifiConnectionState.Authenticating,
        _ => WifiConnectionState.Unknown,
    };

    /// <summary>Converts a channel centre frequency in kHz into the 802.11 channel number.</summary>
    public static int FrequencyToChannel(int frequencyKhz)
    {
        if (frequencyKhz <= 0)
        {
            return 0;
        }

        var mhz = frequencyKhz / 1000.0;

        if (mhz is >= 2412 and <= 2472)
        {
            return (int)Math.Round((mhz - 2407) / 5);
        }

        if (Math.Abs(mhz - 2484) < 1)
        {
            return 14;
        }

        if (mhz is >= 5160 and <= 5895)
        {
            return (int)Math.Round((mhz - 5000) / 5);
        }

        if (mhz is >= 5925 and <= 7125)
        {
            // The 6 GHz plan starts at channel 1 = 5955 MHz.
            return (int)Math.Round((mhz - 5955) / 5) + 1;
        }

        if (mhz is >= 57000 and <= 71000)
        {
            return (int)Math.Round((mhz - 56160) / 2160);
        }

        return 0;
    }

    public static NetworkBand FrequencyToBand(int frequencyKhz)
    {
        if (frequencyKhz <= 0)
        {
            return NetworkBand.Unknown;
        }

        var mhz = frequencyKhz / 1000.0;

        return mhz switch
        {
            < 1000 => NetworkBand.Unknown,
            < 2500 => NetworkBand.Band2_4GHz,
            < 5925 => NetworkBand.Band5GHz,
            < 7125 => NetworkBand.Band6GHz,
            <= 71000 => NetworkBand.Band60GHz,
            _ => NetworkBand.Unknown,
        };
    }

    /// <summary>
    /// Devices sometimes report RSSI as 0. Derive a usable estimate from the link quality percentage
    /// so ranking still works: quality 100 ~ -40 dBm, quality 0 ~ -100 dBm.
    /// </summary>
    public static int EstimateRssiFromQuality(int signalQuality) =>
        signalQuality <= 0 ? -100 : (int)Math.Round(signalQuality / 2.0 - 100);

    public static string DescribeChannel(NetworkBand band, int channel, int frequencyKhz) =>
        channel <= 0
            ? (frequencyKhz > 0 ? $"{frequencyKhz / 1000.0:F0} MHz" : "n/a")
            : $"{band} ch{channel}";
}
