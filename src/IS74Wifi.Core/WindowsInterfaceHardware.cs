using System.Runtime.InteropServices;

namespace IS74Wifi.Core;

internal static unsafe class WindowsInterfaceHardware
{
    private const byte HardwareInterfaceFlag = 0x01;
    private const byte ConnectorPresentFlag = 0x04;

    public static bool TryGet(int interfaceIndex, out bool hardwareInterface, out bool connectorPresent)
    {
        hardwareInterface = false;
        connectorPresent = false;
        if (!OperatingSystem.IsWindows() || interfaceIndex <= 0)
        {
            return false;
        }

        var row = new MibIfRow2 { InterfaceIndex = (uint)interfaceIndex };
        try
        {
            if (GetIfEntry2(ref row) != 0)
            {
                return false;
            }
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }

        hardwareInterface = (row.InterfaceAndOperStatusFlags & HardwareInterfaceFlag) != 0;
        connectorPresent = (row.InterfaceAndOperStatusFlags & ConnectorPresentFlag) != 0;
        return true;
    }

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint GetIfEntry2(ref MibIfRow2 row);

    [StructLayout(LayoutKind.Sequential)]
    private struct MibIfRow2
    {
        public ulong InterfaceLuid;
        public uint InterfaceIndex;
        public Guid InterfaceGuid;
        public fixed char Alias[257];
        public fixed char Description[257];
        public uint PhysicalAddressLength;
        public fixed byte PhysicalAddress[32];
        public fixed byte PermanentPhysicalAddress[32];
        public uint Mtu;
        public uint Type;
        public uint TunnelType;
        public uint MediaType;
        public uint PhysicalMediumType;
        public uint AccessType;
        public uint DirectionType;
        public byte InterfaceAndOperStatusFlags;
        public uint OperStatus;
        public uint AdminStatus;
        public uint MediaConnectState;
        public Guid NetworkGuid;
        public uint ConnectionType;
        public ulong TransmitLinkSpeed;
        public ulong ReceiveLinkSpeed;
        public ulong InOctets;
        public ulong InUcastPkts;
        public ulong InNUcastPkts;
        public ulong InDiscards;
        public ulong InErrors;
        public ulong InUnknownProtos;
        public ulong InUcastOctets;
        public ulong InMulticastOctets;
        public ulong InBroadcastOctets;
        public ulong OutOctets;
        public ulong OutUcastPkts;
        public ulong OutNUcastPkts;
        public ulong OutDiscards;
        public ulong OutErrors;
        public ulong OutUcastOctets;
        public ulong OutMulticastOctets;
        public ulong OutBroadcastOctets;
        public ulong OutQLen;
    }
}
