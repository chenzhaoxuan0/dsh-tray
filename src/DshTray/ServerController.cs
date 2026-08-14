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

    /// <summary>
    /// 持续 HTTP 就绪确认：要求连续两次探测成功才算就绪。
    /// 避免「端口刚监听但服务尚未真正可服务」时误报完成（单次探测太宽松）。
    /// </summary>
    public static bool WaitHttpReady(int port, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        var successes = 0;
        while (Environment.TickCount64 < deadline)
        {
            if (ProbeHttp(port, 2000))
            {
                successes++;
                if (successes >= 2) return true;
            }
            else
            {
                successes = 0;
            }
            Thread.Sleep(800);
        }
        return successes >= 2;
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

    /// <summary>
    /// 结束目标端口的 dsh 服务：只杀端口占用者（/F /T，含其子进程树）。
    /// 绝不枚举/清扫其它 dsh 进程——那会误杀别的端口上运行的服务（如 3080 被 3099 的重启误杀）。
    /// 父链包装（cmd/pnpm/launcher）在服务进程退出后自行退出，无需处理。
    /// 返回端口最终是否释放。
    /// </summary>
    public static bool KillAllDsh(int port, int waitFreeMs = 30000)
    {
        var listener = FindPidOnPort(port);
        if (listener <= 0) return WaitPortFree(port, 1000);
        RunHidden("taskkill", $"/F /T /pid {listener}");
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
    /// 启动 dsh 服务（robust 版，绝不让托盘静默崩溃）。顺序：
    /// 1) 内联启动：全路径 node + 被停服务的 cwd（最可靠，等同 restart-dsh-web.ps1）；
    /// 2) 重放捕获到的命令行（裸可执行名先解析成全路径）；
    /// 3) 应用目录 start-dsh.cmd 兜底（保留用户自定义）。
    /// 每一步都记日志并捕获异常，输出重定向到 ~/.dsh-tray-server.log。
    /// </summary>
    public static bool StartServer(ServerInfo captured, int port, string appDir, string logPath, Action<string>? onProgress = null)
    {
        var serverLog = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh-tray-server.log");

        // 1) 内联启动：全路径 node + 被停服务 cwd（最可靠）
        if (!string.IsNullOrWhiteSpace(captured.WorkingDirectory))
        {
            var node = ResolveExecutable("node");
            if (node is not null)
            {
                // 兜底命令必须带上目标端口，否则会按 dsh 默认 3080 启动（配置非 3080 时起错端口）
                var cmdline = $"cd /d \"{captured.WorkingDirectory}\" && \"\"{node}\" --import tsx/esm apps/cli/src/bin.ts web --host 127.0.0.1 --port {port} >> \"{serverLog}\" 2>&1\"";
                if (SpawnCmd(cmdline, captured.WorkingDirectory, port, logPath, "inline(cwd)", onProgress))
                    return true;
            }
        }

        // 2) 重放捕获到的命令行
        if (!string.IsNullOrWhiteSpace(captured.CommandLine)
            && !string.IsNullOrWhiteSpace(captured.WorkingDirectory))
        {
            string[]? argv = null;
            try { argv = NativeMethods.ParseCommandLine(captured.CommandLine); }
            catch (Exception ex) { Log(logPath, $"重放命令行解析失败: {ex.Message}"); }

            if (argv is { Length: > 0 } && !string.IsNullOrWhiteSpace(argv[0]))
            {
                var exe = ResolveExecutable(argv[0]);
                if (exe is null)
                {
                    Log(logPath, $"重放跳过：无法解析可执行文件 {argv[0]}，改用兜底启动");
                    onProgress?.Invoke($"重放跳过：无法解析 {argv[0]}，改用兜底启动");
                }
                else
                {
                    var args = string.Join(' ', argv.Skip(1).Select(QuoteArg));
                    var cmdline = $"\"\"{exe}\" {args} >> \"{serverLog}\" 2>&1\"";
                    if (SpawnCmd(cmdline, captured.WorkingDirectory, port, logPath, $"replay({exe})", onProgress))
                        return true;
                }
            }
        }

        // 3) start-dsh.cmd 兜底（保留用户自定义）
        var cmd = Path.Combine(appDir, "start-dsh.cmd");
        if (File.Exists(cmd))
        {
            if (SpawnCmd($"\"\"{cmd}\"\"", appDir, port, logPath, "start-dsh.cmd", onProgress))
                return true;
        }
        else
        {
            Log(logPath, $"start-dsh.cmd 不存在: {cmd}");
            onProgress?.Invoke($"start-dsh.cmd 不存在: {cmd}");
        }
        return false;
    }

    /// <summary>通过 cmd.exe 执行一条启动命令并等待端口监听；任何异常记日志并返回 false。
    /// 子进程提前退出（如端口冲突 EADDRINUSE）时快速失败，不傻等 60s。</summary>
    private static bool SpawnCmd(string commandLine, string cwd, int port, string logPath, string label, Action<string>? onProgress = null)
    {
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", $"/d /s /c {commandLine}")
            {
                WorkingDirectory = cwd,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            using var p = Process.Start(psi);
            if (p is null)
            {
                Log(logPath, $"{label}: Process.Start 返回 null");
                onProgress?.Invoke($"{label}: Process.Start 返回 null");
                return false;
            }
            Log(logPath, $"{label}: 已启动 (cwd={cwd}, pid={p.Id})");
            onProgress?.Invoke($"{label}: 已启动 (pid={p.Id}, cwd={cwd})");
            // 等待端口监听或进程提前退出（两者取先，上限 60s）
            var deadline = Environment.TickCount64 + 60000;
            var lastLog = Environment.TickCount64;
            while (Environment.TickCount64 < deadline)
            {
                if (PortOpen(port)) return true;
                if (p.HasExited) break;
                // 每 10s 提示一次等待进度，避免进度窗看起来卡死
                if (Environment.TickCount64 - lastLog >= 10000)
                {
                    lastLog = Environment.TickCount64;
                    var elapsed = (Environment.TickCount64 - deadline + 60000) / 1000;
                    Log(logPath, $"{label}: 等待端口 {port}… (已 {elapsed}s)");
                    onProgress?.Invoke($"{label}: 等待端口 {port}… (已 {elapsed}s)");
                }
                Thread.Sleep(300);
            }
            if (PortOpen(port)) return true;
            if (p.HasExited)
            {
                Log(logPath, $"{label}: 进程提前退出 (exit={p.ExitCode})，可能端口冲突或启动失败（见 .dsh-tray-server.log）");
                onProgress?.Invoke($"{label}: 进程提前退出 (exit={p.ExitCode})");
            }
            else
            {
                Log(logPath, $"{label}: 60s 内端口 {port} 未监听，启动可能失败");
                onProgress?.Invoke($"{label}: 60s 内端口 {port} 未监听");
            }
            return false;
        }
        catch (Exception ex)
        {
            Log(logPath, $"{label}: 启动异常: {ex.Message}");
            onProgress?.Invoke($"{label}: 启动异常: {ex.Message}");
            return false;
        }
    }

    /// <summary>给含空格/引号的参数加引号（cmd 层）。</summary>
    private static string QuoteArg(string arg)
    {
        if (string.IsNullOrEmpty(arg)) return "\"\"";
        if (arg.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return arg;
        return "\"" + arg.Replace("\"", "\\\"") + "\"";
    }

    /// <summary>
    /// 把裸可执行名解析为全路径（托盘进程的 PATH 可能不含 nodejs 目录）。
    /// 已含路径则校验存在性；解析失败返回 null。
    /// </summary>
    public static string? ResolveExecutable(string exe)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(exe)) return null;
            if (Path.IsPathRooted(exe)) return File.Exists(exe) ? exe : null;
            if (string.Equals(exe, "node", StringComparison.OrdinalIgnoreCase)
                || string.Equals(exe, "node.exe", StringComparison.OrdinalIgnoreCase))
            {
                var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                foreach (var cand in new[]
                {
                    Path.Combine(pf, "nodejs", "node.exe"),
                    Path.Combine(pf + " (x86)", "nodejs", "node.exe"),
                })
                {
                    if (File.Exists(cand)) return cand;
                }
            }
            var userPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User);
            foreach (var dir in (userPath ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var full = Path.Combine(dir.Trim(), exe);
                if (File.Exists(full)) return full;
            }
        }
        catch
        {
            // 忽略，返回 null 由调用方降级
        }
        return null;
    }

    /// <summary>默认 start-dsh.cmd 内容（用户在应用目录可自行编辑覆盖；仅作最后兜底）。</summary>
    public static string GenerateDefaultStartCmd(int port)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var harness = Path.Combine(home, "project", "Agent", "deepseek-harness");
        var node = ResolveExecutable("node") ?? "node";
        var log = Path.Combine(home, ".dsh-tray-server.log");
        return $"""
            @echo off
            rem dsh-tray 启动命令。修改本文件可控制 DeepSeek Harness 如何被（重新）启动。
            rem 仅当无法捕获被停服务的命令行/工作目录时才会走到这里（最后兜底）。
            rem 默认：从本机 dev checkout 启动（全路径 node，输出进 .dsh-tray-server.log）。
            title DeepSeek Harness Server
            cd /d "{harness}"
            "{node}" --import tsx/esm apps/cli/src/bin.ts web --host 127.0.0.1 --port {port} >> "{log}" 2>&1
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
