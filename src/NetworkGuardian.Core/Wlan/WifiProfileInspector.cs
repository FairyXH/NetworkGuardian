using System.Security.Cryptography;
using System.Text;
using NetworkGuardian.Core.Models;

namespace NetworkGuardian.Core.Wlan;

/// <summary>Why an existing Windows profile has to be (re)written from the library entry.</summary>
public enum ProfileUpdateReason
{
    /// <summary>The stored profile already matches the library entry.</summary>
    UpToDate = 0,

    /// <summary>No profile of that name exists on the adapter.</summary>
    Missing,

    /// <summary>A profile exists but is not an 802.1X/EAP profile.</summary>
    NotEnterprise,

    /// <summary>The library entry changed since the profile was last written (identity, method, ...).</summary>
    LibraryChanged,
}

/// <summary>
/// Reads the parts of a WLAN profile XML the recovery flow needs, and decides whether an existing
/// profile still matches its library entry. Pure string work: the same rules are unit tested and used
/// by the Windows layer.
/// </summary>
public static class WifiProfileInspector
{
    /// <summary>True when the document is an 802.1X/EAP profile (<c>useOneX</c> / OneX element present).</summary>
    public static bool IsEnterprise(string? profileXml)
    {
        if (string.IsNullOrWhiteSpace(profileXml))
        {
            return false;
        }

        return profileXml.Contains("<useOneX>true</useOneX>", StringComparison.OrdinalIgnoreCase) ||
               profileXml.Contains("OneX xmlns", StringComparison.OrdinalIgnoreCase) ||
               profileXml.Contains("EAPConfig", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when the profile carries an EAP host configuration (i.e. an EAP method is set up).</summary>
    public static bool HasEapConfiguration(string? profileXml) =>
        !string.IsNullOrWhiteSpace(profileXml) &&
        profileXml.Contains("EAPConfig", StringComparison.OrdinalIgnoreCase);

    /// <summary>Network security that requires 802.1X/EAP authentication.</summary>
    public static bool IsEnterpriseSecurity(WifiSecurity security) => security
        is WifiSecurity.WpaEnterprise
        or WifiSecurity.Wpa2Enterprise
        or WifiSecurity.Wpa3Enterprise;

    /// <summary>Reads the first <c>&lt;name&gt;</c> element (the profile name).</summary>
    public static string? TryReadProfileName(string? profileXml) =>
        TryReadElement(profileXml, "name");

    /// <summary>Reads the SSID from the <c>&lt;SSID&gt;&lt;name&gt;</c> element (the hex form is ignored).</summary>
    public static string? TryReadSsid(string? profileXml)
    {
        if (string.IsNullOrWhiteSpace(profileXml))
        {
            return null;
        }

        var ssidBlock = TryReadElementBlock(profileXml, "SSID");
        return ssidBlock is null ? null : TryReadElement(ssidBlock, "name");
    }

    /// <summary>Reads the <c>&lt;authentication&gt;</c> value (WPA2, WPA3, ...).</summary>
    public static string? TryReadAuthentication(string? profileXml) =>
        TryReadElement(profileXml, "authentication");

    /// <summary>Reads the EAP method type id from the EAP host configuration.</summary>
    public static string? TryReadEapMethodType(string? profileXml)
    {
        var block = TryReadElementBlock(profileXml, "EapMethod");
        return block is null ? null : TryReadElement(block, "Type");
    }

    /// <summary>Reads the identity (MSCHAPv2 user name) stored in the profile, if it is visible.</summary>
    public static string? TryReadIdentity(string? profileXml) =>
        TryReadElement(profileXml, "UserName");

    /// <summary>Stable fingerprint of a profile document; used to detect that the library entry changed.</summary>
    public static string Fingerprint(string profileXml) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(profileXml)));

    /// <summary>
    /// Decides whether the Windows profile has to be written from <paramref name="credential"/>.
    /// </summary>
    /// <param name="currentProfileXml">XML read back from the adapter, or null when there is no profile.</param>
    /// <param name="desiredFingerprint">Fingerprint of the XML generated from the library entry.</param>
    public static ProfileUpdateReason Evaluate(
        string? currentProfileXml,
        string desiredFingerprint,
        WifiNetworkCredential credential)
    {
        if (string.IsNullOrWhiteSpace(currentProfileXml))
        {
            return ProfileUpdateReason.Missing;
        }

        if (!IsEnterprise(currentProfileXml))
        {
            return ProfileUpdateReason.NotEnterprise;
        }

        // Nothing was ever written by us: the profile came from Windows or another tool, so the
        // library entry has to be applied once to make it authoritative.
        if (string.IsNullOrWhiteSpace(credential.AppliedFingerprint))
        {
            return ProfileUpdateReason.LibraryChanged;
        }

        return string.Equals(credential.AppliedFingerprint, desiredFingerprint, StringComparison.OrdinalIgnoreCase)
            ? ProfileUpdateReason.UpToDate
            : ProfileUpdateReason.LibraryChanged;
    }

    /// <summary>First <c>&lt;element&gt;value&lt;/element&gt;</c> content, ignoring namespace prefixes.</summary>
    private static string? TryReadElement(string? xml, string element)
    {
        var block = TryReadElementBlock(xml, element);
        if (block is null)
        {
            return null;
        }

        var open = block.IndexOf('>');
        var close = block.LastIndexOf("</", StringComparison.Ordinal);
        if (open < 0 || close <= open)
        {
            return null;
        }

        return block[(open + 1)..close].Trim();
    }

    /// <summary>Complete <c>&lt;element ...&gt;...&lt;/element&gt;</c> slice, handling the namespace prefix form.</summary>
    private static string? TryReadElementBlock(string? xml, string element)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return null;
        }

        // Several elements share a prefix ("<SSID>" inside "<SSIDConfig>"), so every candidate
        // opening tag is checked for a real element boundary before the block is cut out.
        var searchFrom = 0;
        while (searchFrom < xml.Length)
        {
            var open = xml.IndexOf($"<{element}", searchFrom, StringComparison.OrdinalIgnoreCase);
            if (open < 0)
            {
                return null;
            }

            var afterName = open + element.Length + 1;
            if (afterName < xml.Length && xml[afterName] is not ('>' or ' ' or '/' or '\t' or '\r' or '\n'))
            {
                searchFrom = open + 1;
                continue;
            }

            var close = xml.IndexOf($"</{element}>", afterName, StringComparison.OrdinalIgnoreCase);
            if (close < 0)
            {
                searchFrom = open + 1;
                continue;
            }

            var end = close + element.Length + 3;
            return end <= xml.Length ? xml[open..end] : null;
        }

        return null;
    }
}
