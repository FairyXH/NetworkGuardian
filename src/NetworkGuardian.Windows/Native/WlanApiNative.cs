using System.Runtime.InteropServices;

namespace NetworkGuardian.Windows.Native;

/// <summary>
/// P/Invoke declarations for <c>wlanapi.dll</c>.
/// </summary>
/// <remarks>
/// Every structure below was cross-checked against
/// <c>C:\Program Files (x86)\Windows Kits\10\Include\10.0.26100.0\um\wlanapi.h</c>.
/// Variable length structures (the <c>*_LIST</c> types) are declared with only their fixed header so
/// that the managed size is 8 bytes; elements are read with pointer arithmetic from the buffer that
/// the API allocated, which is the only safe way to walk them.
/// </remarks>
internal static unsafe class WlanApiNative
{
    internal const string Dll = "wlanapi.dll";

    internal const uint WlanClientVersionWindowsVista = 1;
    internal const uint WlanClientVersionWindows7 = 2;

    internal const uint WLAN_MAX_NAME_LENGTH = 256;
    internal const uint WLAN_MAX_PHY_TYPE_NUMBER = 8;
    internal const uint WLAN_MAX_RATE_SET_SIZE = 126;

    internal const uint WLAN_NOTIFICATION_SOURCE_NONE = 0x00000000;
    internal const uint WLAN_NOTIFICATION_SOURCE_ONEX = 0x00000004;
    internal const uint WLAN_NOTIFICATION_SOURCE_ACM = 0x00000008;
    internal const uint WLAN_NOTIFICATION_SOURCE_MSM = 0x00000010;
    internal const uint WLAN_NOTIFICATION_SOURCE_SECURITY = 0x00000020;
    internal const uint WLAN_NOTIFICATION_SOURCE_IHV = 0x00000040;
    internal const uint WLAN_NOTIFICATION_SOURCE_HNWK = 0x00000080;
    internal const uint WLAN_NOTIFICATION_SOURCE_DEVICE_SERVICE = 0x00000800;
    internal const uint WLAN_NOTIFICATION_SOURCE_ALL = 0x0000FFFF;

    // WLAN_NOTIFICATION_ACM (base = L2_NOTIFICATION_CODE_PUBLIC_BEGIN = 0)
    internal const uint WlanNotificationAcmStart = 0;
    internal const uint WlanNotificationAcmAutoconfEnabled = 1;
    internal const uint WlanNotificationAcmAutoconfDisabled = 2;
    internal const uint WlanNotificationAcmBackgroundScanEnabled = 3;
    internal const uint WlanNotificationAcmBackgroundScanDisabled = 4;
    internal const uint WlanNotificationAcmBssTypeChange = 5;
    internal const uint WlanNotificationAcmPowerSettingChange = 6;
    internal const uint WlanNotificationAcmScanComplete = 7;
    internal const uint WlanNotificationAcmScanFail = 8;
    internal const uint WlanNotificationAcmConnectionStart = 9;
    internal const uint WlanNotificationAcmConnectionComplete = 10;
    internal const uint WlanNotificationAcmConnectionAttemptFail = 11;
    internal const uint WlanNotificationAcmFilterListChange = 12;
    internal const uint WlanNotificationAcmInterfaceArrival = 13;
    internal const uint WlanNotificationAcmInterfaceRemoval = 14;
    internal const uint WlanNotificationAcmProfileChange = 15;
    internal const uint WlanNotificationAcmProfileNameChange = 16;
    internal const uint WlanNotificationAcmProfilesExhausted = 17;
    internal const uint WlanNotificationAcmNetworkNotAvailable = 18;
    internal const uint WlanNotificationAcmNetworkAvailable = 19;
    internal const uint WlanNotificationAcmDisconnecting = 20;
    internal const uint WlanNotificationAcmDisconnected = 21;
    internal const uint WlanNotificationAcmAdhocNetworkStateChange = 22;
    internal const uint WlanNotificationAcmProfileUnblocked = 23;
    internal const uint WlanNotificationAcmScreenPowerChange = 24;
    internal const uint WlanNotificationAcmProfileBlocked = 25;
    internal const uint WlanNotificationAcmScanListRefresh = 26;
    internal const uint WlanNotificationAcmOperationalStateChange = 27;

    // WLAN_NOTIFICATION_MSM
    internal const uint WlanNotificationMsmStart = 0;
    internal const uint WlanNotificationMsmAssociating = 1;
    internal const uint WlanNotificationMsmAssociated = 2;
    internal const uint WlanNotificationMsmAuthenticating = 3;
    internal const uint WlanNotificationMsmConnected = 4;
    internal const uint WlanNotificationMsmRoamingStart = 5;
    internal const uint WlanNotificationMsmRoamingEnd = 6;
    internal const uint WlanNotificationMsmRadioStateChange = 7;
    internal const uint WlanNotificationMsmSignalQualityChange = 8;
    internal const uint WlanNotificationMsmDisassociating = 9;
    internal const uint WlanNotificationMsmDisconnected = 10;
    internal const uint WlanNotificationMsmPeerJoin = 11;
    internal const uint WlanNotificationMsmPeerLeave = 12;
    internal const uint WlanNotificationMsmAdapterRemoval = 13;
    internal const uint WlanNotificationMsmAdapterOperationModeChange = 14;
    internal const uint WlanNotificationMsmLinkDegraded = 15;
    internal const uint WlanNotificationMsmLinkImproved = 16;

    // WLAN_INTF_OPCODE
    internal const uint WlanIntfOpcodeAutoconfEnabled = 1;
    internal const uint WlanIntfOpcodeBackgroundScanEnabled = 2;
    internal const uint WlanIntfOpcodeMediaStreamingMode = 3;
    internal const uint WlanIntfOpcodeRadioState = 4;
    internal const uint WlanIntfOpcodeBssType = 5;
    internal const uint WlanIntfOpcodeInterfaceState = 6;
    internal const uint WlanIntfOpcodeCurrentConnection = 7;
    internal const uint WlanIntfOpcodeChannelNumber = 8;
    internal const uint WlanIntfOpcodeStatistics = 0x10000101;
    internal const uint WlanIntfOpcodeRssi = 0x10000102;

    // WLAN_AVAILABLE_NETWORK flags
    internal const uint WlanAvailableNetworkConnected = 0x00000001;
    internal const uint WlanAvailableNetworkHasProfile = 0x00000002;
    internal const uint WlanAvailableNetworkConsoleUserProfile = 0x00000004;
    internal const uint WlanAvailableNetworkAutoConnectFailed = 0x00000100;

    // WLAN_PROFILE flags (WlanSetProfile / WlanGetProfile)
    internal const uint WlanProfileGroupPolicy = 0x00000001;
    internal const uint WlanProfileUser = 0x00000002;
    internal const uint WlanProfileGetPlaintextKey = 0x00000004;
    internal const uint WlanProfileConnectionModeSetByClient = 0x00010000;
    internal const uint WlanProfileConnectionModeAuto = 0x00020000;

    /// <summary>
    /// EAP host data storage flag for <c>WlanSetProfileEapXmlUserData</c>. Passing 0 stores the
    /// credentials for the current user only, which is what a per-user profile wants; the other value
    /// (<c>WLAN_SET_EAPHOST_DATA_ALL_USERS</c>) needs administrator rights.
    /// </summary>
    internal const uint WlanSetEaphostDataAllUsers = 0x00000001;

    /// <summary>WlanSetProfile / WlanGetProfile reason codes that are worth naming explicitly.</summary>
    internal const uint WlanReasonProfileBad = 1206;
    internal const uint WlanReasonProfileNameMismatch = 1207;
    internal const uint WlanReasonAccessDenied = 1209;
    internal const uint WlanReasonProfileNotCompatible = 1560;

    internal const uint WlanAvailableNetworkIncludeAllAdhocProfiles = 0x00000001;
    internal const uint WlanAvailableNetworkIncludeAllManualHiddenProfiles = 0x00000002;

    // WLAN_CONNECTION_MODE
    internal const uint WlanConnectionModeProfile = 0;
    internal const uint WlanConnectionModeTemporaryProfile = 1;
    internal const uint WlanConnectionModeDiscoverySecure = 2;
    internal const uint WlanConnectionModeDiscoveryUnsecure = 3;
    internal const uint WlanConnectionModeAuto = 4;

    // DOT11_BSS_TYPE
    internal const uint Dot11BssTypeInfrastructure = 1;
    internal const uint Dot11BssTypeIndependent = 2;
    internal const uint Dot11BssTypeAny = 3;

    // Common WLAN errors.
    internal const uint ERROR_SUCCESS = 0;
    internal const uint ERROR_FILE_NOT_FOUND = 2;
    internal const uint ERROR_ACCESS_DENIED = 5;
    internal const uint ERROR_INVALID_PARAMETER = 87;
    internal const uint ERROR_NOT_FOUND = 1168;
    internal const uint ERROR_NOT_SUPPORTED = 50;
    internal const uint ERROR_INVALID_STATE = 5023;
    internal const uint ERROR_SERVICE_NOT_ACTIVE = 1062;

    internal const uint WLAN_ERROR_ACCESS_DENIED = 5;

    /// <summary>Size of the fixed header of every variable length WLAN list structure.</summary>
    internal const int ListHeaderSize = 8;

    [StructLayout(LayoutKind.Sequential)]
    internal struct DOT11_SSID
    {
        public uint uSSIDLength;
        public fixed byte ucSSID[32];
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WLAN_INTERFACE_INFO_LIST_HEADER
    {
        public uint dwNumberOfItems;
        public uint dwIndex;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WLAN_INTERFACE_INFO
    {
        public Guid InterfaceGuid;
        public fixed char strInterfaceDescription[256];
        public uint isState;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WLAN_AVAILABLE_NETWORK_LIST_HEADER
    {
        public uint dwNumberOfItems;
        public uint dwIndex;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WLAN_AVAILABLE_NETWORK
    {
        public fixed char strProfileName[256];
        public DOT11_SSID dot11Ssid;
        public uint dot11BssType;
        public uint uNumberOfBssids;
        public int bNetworkConnectable;
        public uint wlanNotConnectableReason;
        public uint uNumberOfPhyTypes;
        public fixed uint dot11PhyTypes[8];
        public int bMorePhyTypes;
        public uint wlanSignalQuality;
        public int bSecurityEnabled;
        public uint dot11DefaultAuthAlgorithm;
        public uint dot11DefaultCipherAlgorithm;
        public uint dwFlags;
        public uint dwReserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WLAN_BSS_LIST_HEADER
    {
        public uint dwTotalSize;
        public uint dwNumberOfItems;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WLAN_BSS_ENTRY
    {
        public DOT11_SSID dot11Ssid;
        public uint uPhyId;
        public fixed byte dot11Bssid[6];
        public uint dot11BssType;
        public uint dot11BssPhyType;
        public int lRssi;
        public uint uLinkQuality;
        public byte bInRegDomain;
        public ushort usBeaconPeriod;
        public ulong ullTimestamp;
        public ulong ullHostTimestamp;
        public ushort usCapabilityInformation;
        public uint ulChCenterFrequency;
        public WLAN_RATE_SET wlanRateSet;
        public uint ulIeOffset;
        public uint ulIeSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WLAN_RATE_SET
    {
        public uint uRateSetLength;
        public fixed ushort usRateSet[126];
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WLAN_PROFILE_INFO_LIST_HEADER
    {
        public uint dwNumberOfItems;
        public uint dwIndex;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WLAN_PROFILE_INFO
    {
        public fixed char strProfileName[256];
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WLAN_CONNECTION_PARAMETERS
    {
        public uint wlanConnectionMode;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? strProfile;

        public IntPtr pDot11Ssid;
        public IntPtr pDesiredBssidList;
        public uint dot11BssType;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WLAN_ASSOCIATION_ATTRIBUTES
    {
        public DOT11_SSID dot11Ssid;
        public uint dot11BssType;
        public fixed byte dot11Bssid[6];
        public uint dot11PhyType;
        public uint uDot11PhyIndex;
        public uint wlanSignalQuality;
        public uint ulRxRate;
        public uint ulTxRate;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WLAN_SECURITY_ATTRIBUTES
    {
        public int bSecurityEnabled;
        public int bOneXEnabled;
        public uint dot11AuthAlgorithm;
        public uint dot11CipherAlgorithm;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WLAN_CONNECTION_ATTRIBUTES
    {
        public uint isState;
        public uint wlanConnectionMode;
        public fixed char strProfileName[256];
        public WLAN_ASSOCIATION_ATTRIBUTES wlanAssociationAttributes;
        public WLAN_SECURITY_ATTRIBUTES wlanSecurityAttributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WLAN_NOTIFICATION_DATA
    {
        public uint NotificationSource;
        public uint NotificationCode;
        public Guid InterfaceGuid;
        public uint dwDataSize;
        public IntPtr pData;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DOT11_BSSID_LIST
    {
        public byte HeaderType;
        public byte HeaderRevision;
        public ushort HeaderSize;
        public uint uNumOfEntries;
        public uint uTotalNumOfEntries;
        public fixed byte BSSIDs[6];
    }

    internal const uint WLAN_MAX_PHY_INDEX = 64;
    internal const uint Dot11RadioStateUnknown = 0;
    internal const uint Dot11RadioStateOn = 1;
    internal const uint Dot11RadioStateOff = 2;

    [StructLayout(LayoutKind.Sequential)]
    internal struct WLAN_PHY_RADIO_STATE
    {
        public uint dwPhyIndex;
        public uint dot11SoftwareRadioState;
        public uint dot11HardwareRadioState;
    }

    /// <summary>WLAN_RADIO_STATE: a count followed by WLAN_MAX_PHY_INDEX (64) PHY entries.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct WLAN_RADIO_STATE
    {
        public uint dwNumberOfPhys;
        public fixed uint PhyRadioState[64 * 3];
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate void WlanNotificationCallback(IntPtr notificationData, IntPtr context);

    [DllImport(Dll, ExactSpelling = true)]
    internal static extern uint WlanOpenHandle(
        uint dwClientVersion,
        IntPtr pReserved,
        out uint pdwNegotiatedVersion,
        out IntPtr phClientHandle);

    [DllImport(Dll, ExactSpelling = true)]
    internal static extern uint WlanCloseHandle(IntPtr hClientHandle, IntPtr pReserved);

    [DllImport(Dll, ExactSpelling = true)]
    internal static extern uint WlanEnumInterfaces(
        IntPtr hClientHandle,
        IntPtr pReserved,
        out IntPtr ppInterfaceList);

    [DllImport(Dll, ExactSpelling = true)]
    internal static extern void WlanFreeMemory(IntPtr pMemory);

    [DllImport(Dll, ExactSpelling = true)]
    internal static extern uint WlanRegisterNotification(
        IntPtr hClientHandle,
        uint dwNotifSource,
        int bIgnoreDuplicate,
        WlanNotificationCallback? funcCallback,
        IntPtr pCallbackContext,
        IntPtr pReserved,
        out uint pdwPrevNotifSource);

    [DllImport(Dll, ExactSpelling = true)]
    internal static extern uint WlanScan(
        IntPtr hClientHandle,
        in Guid pInterfaceGuid,
        IntPtr pDot11Ssid,
        IntPtr pIeData,
        IntPtr pReserved);

    [DllImport(Dll, ExactSpelling = true)]
    internal static extern uint WlanGetAvailableNetworkList(
        IntPtr hClientHandle,
        in Guid pInterfaceGuid,
        uint dwFlags,
        IntPtr pReserved,
        out IntPtr ppAvailableNetworkList);

    [DllImport(Dll, ExactSpelling = true)]
    internal static extern uint WlanGetNetworkBssList(
        IntPtr hClientHandle,
        in Guid pInterfaceGuid,
        IntPtr pDot11Ssid,
        uint dot11BssType,
        int bSecurityEnabled,
        IntPtr pReserved,
        out IntPtr ppWlanBssList);

    [DllImport(Dll, ExactSpelling = true)]
    internal static extern uint WlanGetProfileList(
        IntPtr hClientHandle,
        in Guid pInterfaceGuid,
        IntPtr pReserved,
        out IntPtr ppProfileList);

    [DllImport(Dll, ExactSpelling = true)]
    internal static extern uint WlanQueryInterface(
        IntPtr hClientHandle,
        in Guid pInterfaceGuid,
        uint opCode,
        IntPtr pReserved,
        out uint pdwDataSize,
        out IntPtr ppData,
        out uint pWlanOpcodeValueType);

    [DllImport(Dll, ExactSpelling = true)]
    internal static extern uint WlanConnect(
        IntPtr hClientHandle,
        in Guid pInterfaceGuid,
        in WLAN_CONNECTION_PARAMETERS pConnectionParameters,
        IntPtr pReserved);

    [DllImport(Dll, ExactSpelling = true)]
    internal static extern uint WlanDisconnect(
        IntPtr hClientHandle,
        in Guid pInterfaceGuid,
        IntPtr pReserved);

    [DllImport(Dll, CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern uint WlanGetProfile(
        IntPtr hClientHandle,
        in Guid pInterfaceGuid,
        string strProfileName,
        IntPtr pReserved,
        out IntPtr pstrProfileXml,
        ref uint pdwFlags,
        out uint pdwGrantedAccess);

    /// <summary>
    /// Writes (or overwrites) a profile. <paramref name="pdwReasonCode"/> carries the WLAN reason code
    /// when the WLAN service rejects the XML, which is the only feedback about what exactly it disliked.
    /// </summary>
    [DllImport(Dll, CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern uint WlanSetProfile(
        IntPtr hClientHandle,
        in Guid pInterfaceGuid,
        uint dwFlags,
        string strProfileXml,
        string? strAllUserProfileSecurity,
        int bOverwrite,
        IntPtr pReserved,
        out uint pdwReasonCode);

    [DllImport(Dll, CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern uint WlanDeleteProfile(
        IntPtr hClientHandle,
        in Guid pInterfaceGuid,
        string strProfileName,
        IntPtr pReserved);

    /// <summary>
    /// Attaches the account/password (EAP user data) to an existing profile. PEAP-MSCHAPv2 credentials
    /// cannot live in the profile XML - the WLAN service rejects that with reason code 524289 - so this
    /// is the only supported way to make an unattended 802.1X connection work.
    /// </summary>
    [DllImport(Dll, CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern uint WlanSetProfileEapXmlUserData(
        IntPtr hClientHandle,
        in Guid pInterfaceGuid,
        string strProfileName,
        uint dwFlags,
        string strEapXmlUserData,
        IntPtr pReserved);


    [DllImport(Dll, CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern uint WlanReasonCodeToString(
        uint dwReasonCode,
        uint dwBufferSize,
        char[] pStringBuffer,
        IntPtr pReserved);

    /// <summary>WlanStringToSsid takes an LPCWSTR, so it must be marshalled as Unicode.</summary>
    [DllImport(Dll, CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern uint WlanStringToSsid(
        string strSsid,
        byte* pDot11Ssid);

    [DllImport(Dll, ExactSpelling = true)]
    internal static extern uint WlanSetInterface(
        IntPtr hClientHandle,
        in Guid pInterfaceGuid,
        uint opCode,
        uint dwDataSize,
        IntPtr pData,
        IntPtr pReserved);
}
