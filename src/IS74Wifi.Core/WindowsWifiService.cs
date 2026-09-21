using System.Runtime.InteropServices;
using System.Text;

namespace IS74Wifi.Core;

public static partial class WindowsWifiService
{
    private const uint WlanClientVersionLonghorn = 2;
    private const int WlanIntfOpcodeCurrentConnection = 7;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WlanInterfaceInfo
    {
        public Guid InterfaceGuid;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string InterfaceDescription;

        public int InterfaceState;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Dot11Ssid
    {
        public uint SsidLength;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] Ssid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WlanAssociationAttributes
    {
        public Dot11Ssid Dot11Ssid;
        public int Dot11BssType;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)]
        public byte[] Dot11Bssid;

        public int Dot11PhyType;
        public uint Dot11PhyIndex;
        public uint WlanSignalQuality;
        public uint RxRate;
        public uint TxRate;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WlanSecurityAttributes
    {
        [MarshalAs(UnmanagedType.Bool)]
        public bool SecurityEnabled;

        [MarshalAs(UnmanagedType.Bool)]
        public bool OneXEnabled;

        public int AuthAlgorithm;
        public int CipherAlgorithm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WlanConnectionAttributes
    {
        public int InterfaceState;
        public int ConnectionMode;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string ProfileName;

        public WlanAssociationAttributes AssociationAttributes;
        public WlanSecurityAttributes SecurityAttributes;
    }

    public static IReadOnlyList<string> GetConnectedSsids()
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        nint client = 0;
        nint list = 0;

        try
        {
            if (WlanOpenHandle(WlanClientVersionLonghorn, 0, out _, out client) != 0 || client == 0)
            {
                return [];
            }

            if (WlanEnumInterfaces(client, 0, out list) != 0 || list == 0)
            {
                return [];
            }

            var count = Marshal.ReadInt32(list, 0);
            var itemSize = Marshal.SizeOf<WlanInterfaceInfo>();
            var item = list + 8;

            for (var i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<WlanInterfaceInfo>(item);
                item += itemSize;

                nint data = 0;
                try
                {
                    var guid = info.InterfaceGuid;
                    if (WlanQueryInterface(
                            client,
                            ref guid,
                            WlanIntfOpcodeCurrentConnection,
                            0,
                            out _,
                            out data,
                            out _) != 0 ||
                        data == 0)
                    {
                        continue;
                    }

                    var connection = Marshal.PtrToStructure<WlanConnectionAttributes>(data);
                    var nativeSsid = connection.AssociationAttributes.Dot11Ssid;
                    var length = (int)Math.Min(nativeSsid.SsidLength, 32u);
                    if (length <= 0 || nativeSsid.Ssid is null)
                    {
                        continue;
                    }

                    var ssid = Encoding.UTF8.GetString(nativeSsid.Ssid, 0, length);
                    if (!string.IsNullOrEmpty(ssid))
                    {
                        result.Add(ssid);
                    }
                }
                finally
                {
                    if (data != 0)
                    {
                        WlanFreeMemory(data);
                    }
                }
            }
        }
        catch (DllNotFoundException)
        {
            return [];
        }
        catch (EntryPointNotFoundException)
        {
            return [];
        }
        finally
        {
            if (list != 0)
            {
                WlanFreeMemory(list);
            }
            if (client != 0)
            {
                _ = WlanCloseHandle(client, 0);
            }
        }

        return result.ToArray();
    }

    public static bool IsTargetWifiConnected() => GetConnectedSsids().Any(SsidPolicy.IsTarget);

    public static SpeedTestRadioSnapshot GetSpeedTestRadioSnapshot()
    {
        nint client = 0;
        nint list = 0;
        var candidates = new List<(string Ssid, WlanAssociationAttributes Association)>();

        try
        {
            if (WlanOpenHandle(WlanClientVersionLonghorn, 0, out _, out client) != 0 || client == 0)
            {
                return new SpeedTestRadioSnapshot();
            }

            if (WlanEnumInterfaces(client, 0, out list) != 0 || list == 0)
            {
                return new SpeedTestRadioSnapshot();
            }

            var count = Marshal.ReadInt32(list, 0);
            var itemSize = Marshal.SizeOf<WlanInterfaceInfo>();
            var item = list + 8;

            for (var i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<WlanInterfaceInfo>(item);
                item += itemSize;

                nint data = 0;
                try
                {
                    var guid = info.InterfaceGuid;
                    if (WlanQueryInterface(
                            client,
                            ref guid,
                            WlanIntfOpcodeCurrentConnection,
                            0,
                            out _,
                            out data,
                            out _) != 0 ||
                        data == 0)
                    {
                        continue;
                    }

                    var connection = Marshal.PtrToStructure<WlanConnectionAttributes>(data);
                    var currentAssociation = connection.AssociationAttributes;
                    var nativeSsid = currentAssociation.Dot11Ssid;
                    var length = (int)Math.Min(nativeSsid.SsidLength, 32u);
                    if (length <= 0 || nativeSsid.Ssid is null)
                    {
                        continue;
                    }

                    var ssid = Encoding.UTF8.GetString(nativeSsid.Ssid, 0, length);
                    if (!string.IsNullOrEmpty(ssid))
                    {
                        candidates.Add((ssid, currentAssociation));
                    }
                }
                finally
                {
                    if (data != 0)
                    {
                        WlanFreeMemory(data);
                    }
                }
            }
        }
        catch (DllNotFoundException)
        {
            return new SpeedTestRadioSnapshot();
        }
        catch (EntryPointNotFoundException)
        {
            return new SpeedTestRadioSnapshot();
        }
        finally
        {
            if (list != 0)
            {
                WlanFreeMemory(list);
            }
            if (client != 0)
            {
                _ = WlanCloseHandle(client, 0);
            }
        }

        var selected = candidates.FirstOrDefault(item => SsidPolicy.IsTarget(item.Ssid));
        if (string.IsNullOrEmpty(selected.Ssid))
        {
            selected = candidates.FirstOrDefault();
        }
        if (string.IsNullOrEmpty(selected.Ssid))
        {
            return new SpeedTestRadioSnapshot();
        }

        var association = selected.Association;
        return new SpeedTestRadioSnapshot(
            WifiSignalBucket: SignalBucket(association.WlanSignalQuality),
            WifiBand: "unknown",
            ConnectionType: SsidPolicy.IsTarget(selected.Ssid) ? "campus_wifi" : "other_wifi",
            LinkRxMbps: association.RxRate > 0 ? association.RxRate / 1000d : null,
            LinkTxMbps: association.TxRate > 0 ? association.TxRate / 1000d : null,
            WifiProtocol: WifiProtocol(association.Dot11PhyType));
    }

    private static string SignalBucket(uint quality) => quality switch
    {
        >= 80 => "excellent",
        >= 60 => "good",
        >= 40 => "fair",
        _ => "poor"
    };

    private static string? WifiProtocol(int phyType) => phyType switch
    {
        2 or 5 => "802.11b",
        4 => "802.11a",
        6 => "802.11g",
        7 => "802.11n",
        8 => "802.11ac",
        9 => "802.11ad",
        10 => "802.11ax",
        11 => "802.11be",
        _ => null
    };

    [LibraryImport("wlanapi.dll")]
    private static partial uint WlanOpenHandle(
        uint clientVersion,
        nint reserved,
        out uint negotiatedVersion,
        out nint clientHandle);

    [LibraryImport("wlanapi.dll")]
    private static partial uint WlanCloseHandle(nint clientHandle, nint reserved);

    [LibraryImport("wlanapi.dll")]
    private static partial uint WlanEnumInterfaces(nint clientHandle, nint reserved, out nint interfaceList);

    [LibraryImport("wlanapi.dll")]
    private static partial uint WlanQueryInterface(
        nint clientHandle,
        ref Guid interfaceGuid,
        int opCode,
        nint reserved,
        out uint dataSize,
        out nint data,
        out int valueType);

    [LibraryImport("wlanapi.dll")]
    private static partial void WlanFreeMemory(nint memory);
}
