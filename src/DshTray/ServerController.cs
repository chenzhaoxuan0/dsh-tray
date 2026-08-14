using System.Diagnostics;
using System.Net.Sockets;
using System.Text;

namespace DshTray;

/// <summary>
/// DeepSeek Harness 服务进程的查找 / 识别 / 停止 / 重启逻辑。
/// 与 UI 无关，独立成类以便 CLI 模式（--status/--restart/--stop）与托盘共用。
/// </summary>
internal static class ServerController
{
    public sealed record ServerInfo(int Pid, string? CommandLine, string? WorkingDirectory, bool IsDsh);

    /// <summary>命令行里出现这些标记才认为端口上跑的是 dsh（避免误杀其他程序）。</summary>
    private static readonly string[] DshMarkers =
    {
        "dsh", "harness", "bin.ts", "cli/src", "cli\\src", "@deepseek-ai",
    };

    /// <summary>HTTP 探测：端口上有任何 HTTP 响应即认为端口被 Web 服务占用。</summary>
    public static bool ProbeHttp(int port, int timeoutMs = 3000)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };
            for (var i = 0; i < 3; i++)
            {
                try
                {
                    using var resp = client.GetAsync($"http://127.0.0.1:{port}/").GetAwaiter().GetResult();
                    return true; // 有 HTTP 响应（任何状态码）
                }
                catch
                {
                    Thread.Sleep(700);
                }
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>端口是否被监听（TCP 连接是否可建立）。</summary>
    public static bool PortOpen(int port)
    {
        try
        {
            using var c = new TcpClient();
            c.Connect("127.0.0.1", port);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>解析 netstat 输出，找出监听指定 TCP 端口的进程 PID；找不到返回 0。</summary>
    public static int FindPidOnPort(int port)
    {
        try
        {
            var psi = new ProcessStartInfo("netstat", "-ano -p tcp")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return 0;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(3000);
            var token = ":" + port + " ";
            foreach (var line in output.Split('\n'))
            {
                if (!line.Contains("LISTENING", StringComparison.OrdinalIgnoreCase)) continue;
                if (!line.Contains(token)) continue;
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0 && int.TryParse(parts[^1], out var pid) && pid > 0) return pid;
            }
        }
        catch
        {
            // 忽略
        }
        return 0;
    }

    /// <summary>
    /// 汇总当前服务状态：端口上是否有进程、其命令行/工作目录、是否被识别为 dsh。
    /// </summary>
    public static ServerInfo Inspect(int port)
    {
        var pid = FindPidOnPort(port);
        if (pid <= 0) return new ServerInfo(0, null, null, false);

        var cmdline = GetCommandLine(pid);
        var isDsh = ProbeHttp(port) && IsDshCommandLine(cmdline);
        var cwd = isDsh ? NativeMethods.TryGetWorkingDirectory(pid) : null;
        return new ServerInfo(pid, cmdline, cwd, isDsh);
    }

    /// <summary>命令行是否带 dsh 特征标记（大小写不敏感）。</summary>
    public static bool IsDshCommandLine(string? cmdline)
    {
        if (string.IsNullOrWhiteSpace(cmdline)) return false;
        return DshMarkers.Any(m => cmdline.Contains(m, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 读取进程命令行：优先 PowerShell Get-CimInstance（原样返回、无 wmic 的引号包裹/转义污染），
    /// 失败回退 wmic。任何一步失败返回 null。
    /// </summary>
    public static string? GetCommandLine(int pid)
    {
        try
        {
            var psCmd = $"(Get-CimInstance Win32_Process -Filter \"ProcessId={pid}\").CommandLine";
            var psi = new ProcessStartInfo("powershell.exe",
                $"-NoProfile -NonInteractive -Command \"{psCmd}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return null;
            var output = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(8000);
            if (!string.IsNullOrWhiteSpace(output)) return output;
        }
        catch
        {
            // 回退到 wmic
        }

        try
        {
            var psi = new ProcessStartInfo("wmic", $"process where \"ProcessId={pid}\" get CommandLine /value")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return null;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            var parsed = ParseWmicCommandLine(output);
            if (!string.IsNullOrWhiteSpace(parsed)) return parsed;
        }
        catch
        {
            // 忽略
        }
        return null;
    }

    /// <summary>解析 wmic 的 "CommandLine=..." 输出；失败返回 null。</summary>
    private static string? ParseWmicCommandLine(string output)
    {
        foreach (var line in output.Split('\n'))
        {
            var t = line.Trim();
            if (!t.StartsWith("CommandLine=", StringComparison.OrdinalIgnoreCase)) continue;
            var value = t["CommandLine=".Length..].Trim();
            if (value.Length == 0 || value == "=") return null;
            // wmic 会用外层引号包裹整个值，剥掉
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
                value = value[1..^1];
            return value;
        }
        return null;
    }

    /// <summary>
    /// 结束进程树：先温和 taskkill，端口未释放再强制。返回最终端口是否已释放。
    /// </summary>
    public static bool KillProcessTree(int pid, int port, int waitFreeMs = 15000)
    {
        RunHidden("taskkill", $"/pid {pid}");
        if (WaitPortFree(port, 5000)) return true;
        RunHidden("taskkill", $"/T /F /pid {pid}");
        return WaitPortFree(port, waitFreeMs);
    }

    /// <summary>等待端口释放；成功返回 true。</summary>
    public static bool WaitPortFree(int port, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (!PortOpen(port)) return true;
            Thread.Sleep(300);
        }
        return !PortOpen(port);
    }

    /// <summary>等待端口被监听；成功返回 true。</summary>
    public static bool WaitPortOpen(int port, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (PortOpen(port)) return true;
            Thread.Sleep(400);
        }
        return PortOpen(port);
    }

    /// <summary>
    /// 启动 dsh 服务：
    /// 1) 若捕获到原进程命令行 + 工作目录 → 原样重放（最忠实于用户启动方式）；
    /// 2) 否则执行应用目录下的 start-dsh.cmd（不存在时自动生成）。
    /// 均以分离、无窗口方式启动，不依赖托盘进程存活。
    /// </summary>
    public static bool StartServer(ServerInfo captured, int port, string appDir, string logPath)
    {
        // 优先重放捕获到的命令行。工作目录缺失时不用重放——
        // 相对路径脚本（如 apps/cli/src/bin.ts）在错误 cwd 下必然启动失败，此时走 start-dsh.cmd 更可预期。
        if (!string.IsNullOrWhiteSpace(captured.CommandLine)
            && !string.IsNullOrWhiteSpace(captured.WorkingDirectory))
        {
            var argv = NativeMethods.ParseCommandLine(captured.CommandLine);
            if (argv is { Length: > 0 } && !string.IsNullOrWhiteSpace(argv[0]))
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = argv[0],
                        WorkingDirectory = captured.WorkingDirectory!,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden,
                    };
                    if (argv.Length > 1)
                    {
                        foreach (var arg in argv.Skip(1))
                            psi.ArgumentList.Add(arg);
                    }

                    var p = Process.Start(psi);
                    if (p is not null)
                    {
                        Log(logPath, $"replay 启动成功: {captured.CommandLine}  (cwd={psi.WorkingDirectory}, pid={p.Id})");
                        return WaitPortOpen(port, 60000);
                    }
                }
                catch (Exception ex)
                {
                    Log(logPath, $"replay 启动失败: {ex.Message}");
                }
            }
        }

        // 回退：start-dsh.cmd
        var cmd = Path.Combine(appDir, "start-dsh.cmd");
        try
        {
            if (!File.Exists(cmd)) File.WriteAllText(cmd, GenerateDefaultStartCmd(port), Encoding.Default);
            var psi2 = new ProcessStartInfo("cmd.exe", $"/c \"\"{cmd}\"\"")
            {
                WorkingDirectory = appDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            var p2 = Process.Start(psi2);
            if (p2 is not null)
            {
                Log(logPath, $"start-dsh.cmd 启动成功: {cmd} (pid={p2.Id})");
                return WaitPortOpen(port, 60000);
            }
        }
        catch (Exception ex)
        {
            Log(logPath, $"start-dsh.cmd 启动失败: {ex.Message}");
        }
        return false;
    }

    /// <summary>默认 start-dsh.cmd 内容（用户在应用目录可自行编辑覆盖）。</summary>
    public static string GenerateDefaultStartCmd(int port)
    {
        return $"""
            @echo off
            rem dsh-tray 启动命令。修改本文件可控制 DeepSeek Harness 如何被（重新）启动。
            rem 仅当"无法捕获正在运行的 dsh 进程命令行"时才会走到这里。
            rem 默认：优先全局 dsh，其次 npx 拉取。
            title DeepSeek Harness Server
            where dsh >nul 2>&1
            if not errorlevel 1 (
              dsh web --host 127.0.0.1 --port {port}
            ) else (
              npx -y @deepseek-ai/dsh web --host 127.0.0.1 --port {port}
            )
            """;
    }

    /// <summary>隐藏窗口执行一个程序并等待结束（用于 taskkill）。</summary>
    private static void RunHidden(string file, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            p?.WaitForExit(3000);
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>追加一行日志到 %USERPROFILE%\.dsh-tray.log。</summary>
    public static void Log(string logPath, string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\n");
        }
        catch
        {
            // 日志失败不阻断功能
        }
    }
}
