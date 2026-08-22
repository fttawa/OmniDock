using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OmniDock.Interop;

/// <summary>
/// 查询指定进程正在监听的回环端口。
/// 本地代理每次启动的端口都是随机的，所以只能按进程反查，
/// 这样也避免了盲扫所有端口去打扰机器上其它服务。
/// </summary>
internal static class LocalPorts
{
    private const int AfInet = 2;
    private const int TcpTableOwnerPidListener = 3;
    private const uint Loopback = 0x0100007F;  // 127.0.0.1，网络字节序
    private const uint AnyAddress = 0x00000000; // 0.0.0.0 也覆盖回环

    /// <summary>按进程名（不含 .exe）取它监听的回环端口，按端口号排序。</summary>
    internal static IReadOnlyList<int> LoopbackListenersOf(string processName)
    {
        int[] pids;
        try
        {
            pids = Process.GetProcessesByName(processName).Select(p => p.Id).ToArray();
        }
        catch (InvalidOperationException)
        {
            return Array.Empty<int>();
        }

        if (pids.Length == 0)
        {
            return Array.Empty<int>();
        }

        var wanted = new HashSet<int>(pids);
        return AllListeners()
            .Where(entry => wanted.Contains(entry.Pid))
            .Select(entry => entry.Port)
            .Distinct()
            .Order()
            .ToArray();
    }

    private static IEnumerable<(int Port, int Pid)> AllListeners()
    {
        var size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, TcpTableOwnerPidListener, 0);
        if (size <= 0)
        {
            yield break;
        }

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, AfInet, TcpTableOwnerPidListener, 0) != 0)
            {
                yield break;
            }

            var count = Marshal.ReadInt32(buffer);
            var rowSize = Marshal.SizeOf<TcpRowOwnerPid>();
            for (var i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<TcpRowOwnerPid>(buffer + sizeof(int) + i * rowSize);
                if (row.LocalAddress is not (Loopback or AnyAddress))
                {
                    continue;
                }

                // 端口在结构里是网络字节序的两个字节
                var port = (row.LocalPort1 << 8) | row.LocalPort2;
                yield return (port, (int)row.OwningPid);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr table, ref int size, bool sortOrder, int addressFamily, int tableClass, int reserved);

    [StructLayout(LayoutKind.Sequential)]
    private struct TcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddress;
        public byte LocalPort1;
        public byte LocalPort2;
        public byte LocalPort3;
        public byte LocalPort4;
        public uint RemoteAddress;
        public byte RemotePort1;
        public byte RemotePort2;
        public byte RemotePort3;
        public byte RemotePort4;
        public uint OwningPid;
    }
}
