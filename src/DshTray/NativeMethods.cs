using System.Runtime.InteropServices;
using System.Text;

namespace DshTray;

/// <summary>
/// 解析/查询外部进程所需的 Win32 P/Invoke。
/// 全部为只读操作，任何一步失败都由调用方容错降级，不影响托盘主流程。
/// </summary>
internal static class NativeMethods
{
    // ---- CommandLineToArgvW：把进程命令行解析成 argv（与 CRT 相同规则） ----

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW([MarshalAs(UnmanagedType.LPWStr)] string lpCmdLine, out int pNumArgs);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);

    /// <summary>
    /// 把一条命令行按 Windows 规则解析为参数列表。失败返回 null。
    /// </summary>
    public static string[]? ParseCommandLine(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return null;
        var argv = CommandLineToArgvW(commandLine, out var argc);
        if (argv == IntPtr.Zero || argc <= 0) return null;
        try
        {
            var result = new string[argc];
            var ptrSize = IntPtr.Size;
            for (var i = 0; i < argc; i++)
            {
                var p = Marshal.ReadIntPtr(argv, i * ptrSize);
                result[i] = Marshal.PtrToStringUni(p) ?? string.Empty;
            }
            return result;
        }
        finally
        {
            LocalFree(argv);
        }
    }

    // ---- 读取进程工作目录（PEB 法，x64 布局） ----

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint PROCESS_VM_READ = 0x0010;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress,
        [Out] byte[] lpBuffer, int dwSize, out IntPtr lpNumberOfBytesRead);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr processHandle, int processInformationClass,
        out PROCESS_BASIC_INFORMATION processInformation, int processInformationLength, out int returnLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr Reserved1;      // 0x00
        public IntPtr PebBaseAddress; // 0x08
        public IntPtr Reserved2_0;    // 0x10
        public IntPtr Reserved2_1;    // 0x18
        public IntPtr UniqueProcessId;// 0x20
        public IntPtr Reserved3;      // 0x28
    }

    /// <summary>
    /// 读取 64 位进程的当前工作目录（PEB → RTL_USER_PROCESS_PARAMETERS → CurrentDirectory.DosPath）。
    /// 仅支持同架构（x64→x64）；任何一步失败返回 null。32 位进程不适用，调用方需自行降级。
    /// </summary>
    public static string? TryGetWorkingDirectory(int pid)
    {
        if (pid <= 0) return null;
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var status = NtQueryInformationProcess(h, 0 /*ProcessBasicInformation*/,
                out var pbi, Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(), out _);
            if (status != 0 || pbi.PebBaseAddress == IntPtr.Zero) return null;

            // PEB + 0x20 (x64) → ProcessParameters 指针
            if (!ReadPtr(h, IntPtr.Add(pbi.PebBaseAddress, 0x20), out var processParams) || processParams == IntPtr.Zero)
                return null;

            // RTL_USER_PROCESS_PARAMETERS + 0x38 (x64) → CURDIR { UNICODE_STRING DosPath; HANDLE Handle; }
            // UNICODE_STRING: Length(2) MaxLen(2) Buffer(8) → 字符串字节长度在 +0x38，Buffer 指针在 +0x40
            // 注意：目标进程内存只能用 ReadProcessMemory 读，禁止直接解引用跨进程指针。
            var head = new byte[16];
            if (!ReadProcessMemory(h, IntPtr.Add(processParams, 0x38), head, head.Length, out _))
                return null;
            var byteLength = BitConverter.ToUInt16(head, 0);
            if (!ReadPtr(h, IntPtr.Add(processParams, 0x40), out var buffer) || buffer == IntPtr.Zero)
                return null;
            if (byteLength == 0 || byteLength > 4096) return null;

            var raw = new byte[byteLength];
            if (!ReadProcessMemory(h, buffer, raw, raw.Length, out _)) return null;
            return Encoding.Unicode.GetString(raw).TrimEnd('\0');
        }
        catch
        {
            return null;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    private static bool ReadPtr(IntPtr h, IntPtr address, out IntPtr value)
    {
        value = IntPtr.Zero;
        var buf = new byte[IntPtr.Size];
        if (!ReadProcessMemory(h, address, buf, buf.Length, out _)) return false;
        value = IntPtr.Size == 8
            ? new IntPtr(BitConverter.ToInt64(buf, 0))
            : new IntPtr(BitConverter.ToInt32(buf, 0));
        return true;
    }
}
