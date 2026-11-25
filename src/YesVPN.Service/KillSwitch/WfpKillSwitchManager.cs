using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using YesVPN.Service.Logging;

namespace YesVPN.Service.KillSwitch;

/// <summary>
/// Windows Filtering Platform (WFP) kill switch using a dynamic engine session.
/// Because FWPM_SESSION_FLAG_DYNAMIC is used, Windows automatically removes all active filters
/// if the service process crashes or is killed, while DisableAsync/CleanupStaleRulesAsync
/// also cleans up explicitly and removes any legacy netsh YesVPN_* rules.
/// </summary>
public sealed class WfpKillSwitchManager : IKillSwitchManager
{
    private static readonly Guid YesVpnSubLayerKey = new("9E2B6E41-5C78-4C3B-9D82-1A7C54E10F01");

    // Standard WFP Layer & Condition GUIDs (from fwpmu.h)
    private static readonly Guid FwpmLayerAleAuthConnectV4 = new("c38d57d1-05a7-4c33-904f-7fbceee60e82");
    private static readonly Guid FwpmLayerAleAuthConnectV6 = new("4a72393b-319f-44bc-84c3-ba54dcb3b6b4");
    private static readonly Guid FwpmConditionIpRemoteAddress = new("b235ae9a-1d64-49b8-a44c-5ff3d9095045");
    private static readonly Guid FwpmConditionIpLocalAddress = new("d9ee00de-c1ef-4617-bfe3-ffd8f5a08957");
    private static readonly Guid FwpmConditionIpRemotePort = new("c35a604d-d22b-4e1a-91b4-68f674ee674b");
    private static readonly Guid FwpmConditionIpProtocol = new("3971ef2b-623e-4f9a-8cb1-6e79b806b9a7");
    private static readonly Guid FwpmConditionAleAppId = new("d78e1e87-8644-4ea5-9437-d809ecefc971");

    private const uint FwpmSessionFlagDynamic = 0x00000001;
    private const uint RpcCAuthnWinnt = 10;
    private const uint FwpActionBlock = 0x00000001 | 0x00001000;  // FWP_ACTION_FLAG_TERMINATING | 1
    private const uint FwpActionPermit = 0x00000002 | 0x00001000; // FWP_ACTION_FLAG_TERMINATING | 2
    private const uint FwpMatchEqual = 0;
    public const uint FwpEmpty = 0;
    public const uint FwpUint8 = 1;
    public const uint FwpUint16 = 2;
    public const uint FwpUint32 = 3;
    public const uint FwpUint64 = 4;
    public const uint FwpByteBlobType = 12;
    public const uint FwpV4AddrMask = 0x100; // 256 (FWP_SINGLE_DATA_TYPE_MAX = 0xff, followed by FWP_V4_ADDR_MASK)
    public const uint FwpV6AddrMask = 0x101; // 257

    private readonly LogRingBuffer _logs;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IntPtr _engineHandle;
    private readonly List<ulong> _filterIds = new();

    public bool IsActive { get; private set; }

    public WfpKillSwitchManager(LogRingBuffer logs)
    {
        _logs = logs;
    }

    public async Task EnableAsync(
        string singBoxExePath,
        IReadOnlyList<string> proxyHostsToAllow,
        bool blockNonDnsUdp,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            RemoveWfpSessionUnsafe();

            if (!IsRunningElevated())
            {
                _logs.Append("KillSwitch", "Service is not running elevated; skipping WFP kernel filter installation (dev mode).");
                IsActive = false;
                return;
            }

            var session = new FwpmSession0
            {
                displayData = CreateDisplayData("YesVPN Kill Switch Session", "Dynamic WFP kill switch for YesVPN"),
                flags = FwpmSessionFlagDynamic
            };

            IntPtr engine = IntPtr.Zero;
            var status = FwpmEngineOpen0(null, RpcCAuthnWinnt, IntPtr.Zero, ref session, out engine);
            FreeDisplayData(ref session.displayData);

            if (status != 0)
            {
                throw new InvalidOperationException($"FwpmEngineOpen0 failed with 0x{status:X8}.");
            }

            _engineHandle = engine;

            // Ensure stale sublayer with our key does not remain
            var subLayerKey = YesVpnSubLayerKey;
            _ = FwpmSubLayerDeleteByKey0(_engineHandle, ref subLayerKey);

            var subLayer = new FwpmSubLayer0
            {
                subLayerKey = YesVpnSubLayerKey,
                displayData = CreateDisplayData("YesVPN Kill Switch", "YesVPN tunnel-only traffic enforcement"),
                weight = 0xFFFF
            };
            status = FwpmSubLayerAdd0(_engineHandle, ref subLayer, IntPtr.Zero);
            FreeDisplayData(ref subLayer.displayData);

            if (status != 0)
            {
                throw new InvalidOperationException($"FwpmSubLayerAdd0 failed with 0x{status:X8}.");
            }

            InstallFiltersUnsafe(singBoxExePath, proxyHostsToAllow, blockNonDnsUdp);
            IsActive = true;
            _logs.Append("KillSwitch", $"WFP kill switch enabled ({_filterIds.Count} filters, DNS port 53 blocked outside tunnel).");
        }
        catch (Exception ex)
        {
            RemoveWfpSessionUnsafe();
            IsActive = false;
            _logs.Append("KillSwitch", $"Failed to enable WFP kill switch: {ex.Message}");
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DisableAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var wasActive = IsActive || _engineHandle != IntPtr.Zero;
            RemoveWfpSessionUnsafe();
            IsActive = false;
            if (wasActive)
            {
                _logs.Append("KillSwitch", "WFP kill switch disabled and filters removed.");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CleanupStaleRulesAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            RemoveWfpSessionUnsafe();
            IsActive = false;

            if (!IsRunningElevated())
            {
                return;
            }

            var session = new FwpmSession0
            {
                displayData = CreateDisplayData("YesVPN Cleanup Session", "Cleanup stale YesVPN WFP rules"),
                flags = FwpmSessionFlagDynamic
            };

            if (FwpmEngineOpen0(null, RpcCAuthnWinnt, IntPtr.Zero, ref session, out var engine) == 0)
            {
                try
                {
                    var subLayerKey = YesVpnSubLayerKey;
                    _ = FwpmSubLayerDeleteByKey0(engine, ref subLayerKey);
                }
                finally
                {
                    _ = FwpmEngineClose0(engine);
                }
            }
            FreeDisplayData(ref session.displayData);

            await CleanupLegacyNetshRulesAsync(ct).ConfigureAwait(false);
            _logs.Append("KillSwitch", "Stale kill switch rules cleanup complete.");
        }
        finally
        {
            _gate.Release();
        }
    }

    private void InstallFiltersUnsafe(string singBoxExePath, IReadOnlyList<string> proxyHostsToAllow, bool blockNonDnsUdp)
    {
        // 1. Permit loopback IPv4 & IPv6 (weight 100)
        AddAddressPermitFilter(FwpmLayerAleAuthConnectV4, "YesVPN Allow Loopback v4", 100, "127.0.0.0", 8, isLocal: false);
        AddAddressPermitFilter(FwpmLayerAleAuthConnectV6, "YesVPN Allow Loopback v6", 100, "::1", 128, isLocal: false);

        // 2. Permit sing-box.exe and YesVPN.Service.exe (for LoadBalance upstream dialing) outbound (weight 95)
        if (File.Exists(singBoxExePath))
        {
            AddAppPermitFilter(FwpmLayerAleAuthConnectV4, "YesVPN Allow sing-box v4", 95, singBoxExePath);
            AddAppPermitFilter(FwpmLayerAleAuthConnectV6, "YesVPN Allow sing-box v6", 95, singBoxExePath);
        }

        var selfExe = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(selfExe) && File.Exists(selfExe))
        {
            AddAppPermitFilter(FwpmLayerAleAuthConnectV4, "YesVPN Allow Service v4", 95, selfExe);
            AddAppPermitFilter(FwpmLayerAleAuthConnectV6, "YesVPN Allow Service v6", 95, selfExe);
        }

        // 3. Permit traffic originating from Wintun tunnel interface IP (172.19.0.0/30 & IPv6 ULA) (weight 90)
        AddAddressPermitFilter(FwpmLayerAleAuthConnectV4, "YesVPN Allow TUN v4", 90, "172.19.0.0", 30, isLocal: true);
        AddAddressPermitFilter(FwpmLayerAleAuthConnectV6, "YesVPN Allow TUN v6", 90, "fdfe:dcba:9876::", 126, isLocal: true);

        // 4. Permit private LAN ranges (weight 80)
        AddAddressPermitFilter(FwpmLayerAleAuthConnectV4, "YesVPN Allow LAN 10/8", 80, "10.0.0.0", 8, isLocal: false);
        AddAddressPermitFilter(FwpmLayerAleAuthConnectV4, "YesVPN Allow LAN 172.16/12", 80, "172.16.0.0", 12, isLocal: false);
        AddAddressPermitFilter(FwpmLayerAleAuthConnectV4, "YesVPN Allow LAN 192.168/16", 80, "192.168.0.0", 16, isLocal: false);
        AddAddressPermitFilter(FwpmLayerAleAuthConnectV4, "YesVPN Allow LinkLocal", 80, "169.254.0.0", 16, isLocal: false);
        AddAddressPermitFilter(FwpmLayerAleAuthConnectV6, "YesVPN Allow ULA v6", 80, "fc00::", 7, isLocal: false);
        AddAddressPermitFilter(FwpmLayerAleAuthConnectV6, "YesVPN Allow LinkLocal v6", 80, "fe80::", 10, isLocal: false);

        // 5. Permit DHCP outbound UDP ports 67/68 (weight 75)
        AddProtocolPortFilter(FwpmLayerAleAuthConnectV4, "YesVPN Allow DHCP 67", 75, FwpActionPermit, 17, 67);
        AddProtocolPortFilter(FwpmLayerAleAuthConnectV4, "YesVPN Allow DHCP 68", 75, FwpActionPermit, 17, 68);

        // 6. Permit configured proxy IP addresses if resolved (weight 70) so health probes / sing-box work
        foreach (var host in proxyHostsToAllow)
        {
            if (IPAddress.TryParse(host, out var ip))
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork)
                {
                    AddAddressPermitFilter(FwpmLayerAleAuthConnectV4, "YesVPN Allow Proxy IPv4", 70, ip.ToString(), 32, isLocal: false);
                }
                else if (ip.AddressFamily == AddressFamily.InterNetworkV6)
                {
                    AddAddressPermitFilter(FwpmLayerAleAuthConnectV6, "YesVPN Allow Proxy IPv6", 70, ip.ToString(), 128, isLocal: false);
                }
            }
        }

        // 7. Block outbound DNS port 53 (UDP & TCP) outside TUN/sing-box (weight 50)
        AddProtocolPortFilter(FwpmLayerAleAuthConnectV4, "YesVPN Block DNS UDP 53 v4", 50, FwpActionBlock, 17, 53);
        AddProtocolPortFilter(FwpmLayerAleAuthConnectV4, "YesVPN Block DNS TCP 53 v4", 50, FwpActionBlock, 6, 53);
        AddProtocolPortFilter(FwpmLayerAleAuthConnectV6, "YesVPN Block DNS UDP 53 v6", 50, FwpActionBlock, 17, 53);
        AddProtocolPortFilter(FwpmLayerAleAuthConnectV6, "YesVPN Block DNS TCP 53 v6", 50, FwpActionBlock, 6, 53);

        // 8. Optionally block non-DNS UDP outside tunnel/sing-box (weight 40)
        if (blockNonDnsUdp)
        {
            AddProtocolFilter(FwpmLayerAleAuthConnectV4, "YesVPN Block UDP v4", 40, FwpActionBlock, 17);
            AddProtocolFilter(FwpmLayerAleAuthConnectV6, "YesVPN Block UDP v6", 40, FwpActionBlock, 17);
        }

        // 9. Block all other outbound IPv4/IPv6 connections outside tunnel/sing-box (weight 10)
        AddCatchAllFilter(FwpmLayerAleAuthConnectV4, "YesVPN KillSwitch Block All v4", 10, FwpActionBlock);
        AddCatchAllFilter(FwpmLayerAleAuthConnectV6, "YesVPN KillSwitch Block All v6", 10, FwpActionBlock);
    }

    private void AddAppPermitFilter(Guid layerKey, string name, ulong weight, string exePath)
    {
        var status = FwpmGetAppIdFromFileName0(exePath, out var appIdBlobPtr);
        if (status != 0)
        {
            _logs.Append("KillSwitch", $"Warning: FwpmGetAppIdFromFileName0 failed for {exePath} (0x{status:X8}).");
            return;
        }

        try
        {
            var cond = new FwpmFilterCondition0
            {
                fieldKey = FwpmConditionAleAppId,
                matchType = FwpMatchEqual,
                conditionValue = new FwpValue0 { type = FwpByteBlobType, value = (ulong)appIdBlobPtr.ToInt64() }
            };
            AddFilter(layerKey, name, weight, FwpActionPermit, [cond]);
        }
        finally
        {
            FwpmFreeMemory0(ref appIdBlobPtr);
        }
    }

    private void AddAddressPermitFilter(Guid layerKey, string name, ulong weight, string ipText, byte prefixLength, bool isLocal)
    {
        var ip = IPAddress.Parse(ipText);
        var fieldKey = isLocal ? FwpmConditionIpLocalAddress : FwpmConditionIpRemoteAddress;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = ip.GetAddressBytes();
            var hostOrderIp = (uint)((bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3]);
            var mask = prefixLength == 0 ? 0u : uint.MaxValue << (32 - prefixLength);

            var v4 = new FwpV4AddrAndMask { addr = hostOrderIp, mask = mask };
            var ptr = Marshal.AllocHGlobal(Marshal.SizeOf<FwpV4AddrAndMask>());
            try
            {
                Marshal.StructureToPtr(v4, ptr, false);
                var cond = new FwpmFilterCondition0
                {
                    fieldKey = fieldKey,
                    matchType = FwpMatchEqual,
                    conditionValue = new FwpValue0 { type = FwpV4AddrMask, value = (ulong)ptr.ToInt64() }
                };
                AddFilter(layerKey, name, weight, FwpActionPermit, [cond]);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }
        else
        {
            var v6 = new FwpV6AddrAndMask { addr = ip.GetAddressBytes(), prefixLength = prefixLength };
            var ptr = Marshal.AllocHGlobal(Marshal.SizeOf<FwpV6AddrAndMask>());
            try
            {
                Marshal.StructureToPtr(v6, ptr, false);
                var cond = new FwpmFilterCondition0
                {
                    fieldKey = fieldKey,
                    matchType = FwpMatchEqual,
                    conditionValue = new FwpValue0 { type = FwpV6AddrMask, value = (ulong)ptr.ToInt64() }
                };
                AddFilter(layerKey, name, weight, FwpActionPermit, [cond]);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }
    }

    private void AddProtocolPortFilter(Guid layerKey, string name, ulong weight, uint actionType, byte protocol, ushort remotePort)
    {
        var condProto = new FwpmFilterCondition0
        {
            fieldKey = FwpmConditionIpProtocol,
            matchType = FwpMatchEqual,
            conditionValue = new FwpValue0 { type = FwpUint8, value = protocol }
        };
        var condPort = new FwpmFilterCondition0
        {
            fieldKey = FwpmConditionIpRemotePort,
            matchType = FwpMatchEqual,
            conditionValue = new FwpValue0 { type = FwpUint16, value = remotePort }
        };
        AddFilter(layerKey, name, weight, actionType, [condProto, condPort]);
    }

    private void AddProtocolFilter(Guid layerKey, string name, ulong weight, uint actionType, byte protocol)
    {
        var condProto = new FwpmFilterCondition0
        {
            fieldKey = FwpmConditionIpProtocol,
            matchType = FwpMatchEqual,
            conditionValue = new FwpValue0 { type = FwpUint8, value = protocol }
        };
        AddFilter(layerKey, name, weight, actionType, [condProto]);
    }

    private void AddCatchAllFilter(Guid layerKey, string name, ulong weight, uint actionType) =>
        AddFilter(layerKey, name, weight, actionType, Array.Empty<FwpmFilterCondition0>());

    private void AddFilter(Guid layerKey, string name, ulong weight, uint actionType, FwpmFilterCondition0[] conditions)
    {
        var weightPtr = Marshal.AllocHGlobal(sizeof(ulong));
        var condPtr = IntPtr.Zero;
        var display = CreateDisplayData(name, "YesVPN Kill Switch Rule");
        try
        {
            Marshal.WriteInt64(weightPtr, unchecked((long)weight));

            if (conditions.Length > 0)
            {
                var condSize = Marshal.SizeOf<FwpmFilterCondition0>();
                condPtr = Marshal.AllocHGlobal(condSize * conditions.Length);
                for (var i = 0; i < conditions.Length; i++)
                {
                    Marshal.StructureToPtr(conditions[i], IntPtr.Add(condPtr, i * condSize), false);
                }
            }

            var filter = new FwpmFilter0
            {
                filterKey = Guid.NewGuid(),
                displayData = display,
                layerKey = layerKey,
                subLayerKey = YesVpnSubLayerKey,
                weight = new FwpValue0 { type = FwpUint64, value = (ulong)weightPtr.ToInt64() },
                numFilterConditions = (uint)conditions.Length,
                filterCondition = condPtr,
                action = new FwpmAction0 { type = actionType }
            };

            var status = FwpmFilterAdd0(_engineHandle, ref filter, IntPtr.Zero, out var id);
            if (status != 0)
            {
                throw new InvalidOperationException($"FwpmFilterAdd0('{name}') failed with 0x{status:X8}.");
            }
            _filterIds.Add(id);
        }
        finally
        {
            FreeDisplayData(ref display);
            Marshal.FreeHGlobal(weightPtr);
            if (condPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(condPtr);
            }
        }
    }

    private void RemoveWfpSessionUnsafe()
    {
        if (_engineHandle == IntPtr.Zero)
        {
            _filterIds.Clear();
            return;
        }

        foreach (var id in _filterIds)
        {
            _ = FwpmFilterDeleteById0(_engineHandle, id);
        }
        _filterIds.Clear();

        var subLayerKey = YesVpnSubLayerKey;
        _ = FwpmSubLayerDeleteByKey0(_engineHandle, ref subLayerKey);
        _ = FwpmEngineClose0(_engineHandle);
        _engineHandle = IntPtr.Zero;
    }

    private static async Task CleanupLegacyNetshRulesAsync(CancellationToken ct)
    {
        string[] legacyRules =
        [
            "YesVPN_KillSwitch_BlockOut",
            "YesVPN_KillSwitch_AllowLoopback",
            "YesVPN_KillSwitch_AllowTun",
            "YesVPN_KillSwitch_AllowProxy",
            "YesVPN_KillSwitch_BlockDns",
            "YesVPN_BlockNonDnsUdp"
        ];

        foreach (var rule in legacyRules)
        {
            try
            {
                using var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = "netsh",
                    Arguments = $"advfirewall firewall delete rule name=\"{rule}\"",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                });
                if (proc is not null)
                {
                    await proc.WaitForExitAsync(ct).ConfigureAwait(false);
                }
            }
            catch
            {
                // Best-effort cleanup
            }
        }
    }

    private static bool IsRunningElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator) || identity.IsSystem;
    }

    private static FwpmDisplayData0 CreateDisplayData(string name, string description) => new()
    {
        name = Marshal.StringToCoTaskMemUni(name),
        description = Marshal.StringToCoTaskMemUni(description)
    };

    private static void FreeDisplayData(ref FwpmDisplayData0 data)
    {
        if (data.name != IntPtr.Zero)
        {
            Marshal.FreeCoTaskMem(data.name);
            data.name = IntPtr.Zero;
        }
        if (data.description != IntPtr.Zero)
        {
            Marshal.FreeCoTaskMem(data.description);
            data.description = IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        RemoveWfpSessionUnsafe();
        _gate.Dispose();
    }

    #region Native WFP Interop

    [StructLayout(LayoutKind.Sequential)]
    private struct FwpmDisplayData0
    {
        public IntPtr name;
        public IntPtr description;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FwpmSession0
    {
        public Guid sessionKey;
        public FwpmDisplayData0 displayData;
        public uint flags;
        public uint txnWaitTimeoutInMSec;
        public uint processId;
        public IntPtr sid;
        public IntPtr username;
        public int kernelMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FwpByteBlob
    {
        public uint size;
        public IntPtr data;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FwpmSubLayer0
    {
        public Guid subLayerKey;
        public FwpmDisplayData0 displayData;
        public uint flags;
        public IntPtr providerKey;
        public FwpByteBlob providerData;
        public ushort weight;
    }

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct FwpValue0
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public ulong value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FwpmFilterCondition0
    {
        public Guid fieldKey;
        public uint matchType;
        public FwpValue0 conditionValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FwpmAction0
    {
        public uint type;
        public Guid filterType;
    }

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct FwpmContextUnion
    {
        [FieldOffset(0)] public ulong rawContext;
        [FieldOffset(0)] public Guid providerContextKey;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FwpmFilter0
    {
        public Guid filterKey;
        public FwpmDisplayData0 displayData;
        public uint flags;
        public IntPtr providerKey;
        public FwpByteBlob providerData;
        public Guid layerKey;
        public Guid subLayerKey;
        public FwpValue0 weight;
        public uint numFilterConditions;
        public IntPtr filterCondition;
        public FwpmAction0 action;
        public FwpmContextUnion context;
        public IntPtr reserved;
        public ulong filterId;
        public FwpValue0 effectiveWeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FwpV4AddrAndMask
    {
        public uint addr;
        public uint mask;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FwpV6AddrAndMask
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] addr;
        public byte prefixLength;
    }

    [DllImport("fwpuclnt.dll", CharSet = CharSet.Unicode)]
    private static extern uint FwpmEngineOpen0(
        string? serverName,
        uint authnService,
        IntPtr authIdentity,
        ref FwpmSession0 session,
        out IntPtr engineHandle);

    [DllImport("fwpuclnt.dll")]
    private static extern uint FwpmEngineClose0(IntPtr engineHandle);

    [DllImport("fwpuclnt.dll")]
    private static extern uint FwpmSubLayerAdd0(IntPtr engineHandle, ref FwpmSubLayer0 subLayer, IntPtr sd);

    [DllImport("fwpuclnt.dll")]
    private static extern uint FwpmSubLayerDeleteByKey0(IntPtr engineHandle, ref Guid key);

    [DllImport("fwpuclnt.dll")]
    private static extern uint FwpmFilterAdd0(IntPtr engineHandle, ref FwpmFilter0 filter, IntPtr sd, out ulong id);

    [DllImport("fwpuclnt.dll")]
    private static extern uint FwpmFilterDeleteById0(IntPtr engineHandle, ulong id);

    [DllImport("fwpuclnt.dll", CharSet = CharSet.Unicode)]
    private static extern uint FwpmGetAppIdFromFileName0(string fileName, out IntPtr appId);

    [DllImport("fwpuclnt.dll")]
    private static extern void FwpmFreeMemory0(ref IntPtr p);

    #endregion
}
