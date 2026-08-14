using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace DshTray;

internal static class Program
{
    private const int SW_SHOW = 5;

    /// <summary>日志路径：%USERPROFILE%\.dsh-tray.log</summary>
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh-tray.log");

    /// <summary>应用目录（托盘 exe / start-dsh.cmd / tray.config.json 所在目录）。</summary>
    private static readonly string AppDir = AppContext.BaseDirectory;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    /// <summary>配置文件（应用目录下 tray.config.json，可缺省）。</summary>
    private static readonly string ConfigPath = Path.Combine(AppDir, "tray.config.json");

    private sealed record TrayConfig(int Port = 3080, bool StopServerOnExit = true);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool DestroyIcon(IntPtr handle);

    [STAThread]
    private static int Main(string[] args)
    {
        var cfg = LoadConfig();

        // ---- CLI 模式（无托盘）：便于测试与脚本化 ----
        var mode = args.FirstOrDefault(a => a is "--status" or "--test-capture" or "--restart" or "--stop");
        if (mode is not null)
        {
            var port = ReadPortOverride(args) ?? cfg.Port;
            return RunCli(mode, port);
        }

        // ---- GUI 模式：托盘 ----
        // 单实例：重复启动直接退出（一个托盘图标就够）
        using var mutex = new Mutex(true, "Local\\DshTray.SingleInstance", out var firstInstance);
        if (!firstInstance) return 0;

        ServerController.Log(LogPath, $"dsh-tray 启动 (pid={Environment.ProcessId}, port={cfg.Port}, appDir={AppDir})");
        EnsureStartCmd(cfg.Port);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var icon = LoadEmbeddedIcon();
        var ctx = new TrayAppContext(cfg, icon, LogPath, AppDir);
        Application.Run(ctx);
        if (icon is not null)
        {
            try { DestroyIcon(icon.Handle); } catch { }
            icon.Dispose();
        }
        ServerController.Log(LogPath, "dsh-tray 退出");
        return 0;
    }

    private static int RunCli(string cmd, int port)
    {
        switch (cmd)
        {
            case "--status":
            {
                var info = ServerController.Inspect(port);
                Console.WriteLine($"port={port} pid={info.Pid} isDsh={info.IsDsh}");
                Console.WriteLine($"commandLine={info.CommandLine}");
                Console.WriteLine($"workingDirectory={info.WorkingDirectory}");
                return info.Pid > 0 ? 0 : 1;
            }
            case "--test-capture":
            {
                // 强制捕获 cwd（即使 HTTP 探测失败也尝试），验证 PEB 读取
                var pid = ServerController.FindPidOnPort(port);
                Console.WriteLine($"port={port} pid={pid}");
                if (pid > 0)
                {
                    var cmdline = ServerController.GetCommandLine(pid);
                    var cwd = NativeMethods.TryGetWorkingDirectory(pid);
                    Console.WriteLine($"commandLine={cmdline}");
                    Console.WriteLine($"workingDirectory={cwd}");
                    if (cmdline is not null)
                    {
                        var argv = NativeMethods.ParseCommandLine(cmdline);
                        Console.WriteLine($"argv[0]={argv?[0]} args={argv?.Length - 1}");
                    }
                    return cwd is null ? 1 : 0;
                }
                return 1;
            }
            case "--restart":
            {
                var ok = RestartOnce(port);
                Console.WriteLine(ok ? "restart ok" : "restart failed (see log)");
                return ok ? 0 : 1;
            }
            case "--stop":
            {
                var info = ServerController.Inspect(port);
                if (info.Pid > 0 && info.IsDsh)
                {
                    var freed = ServerController.KillProcessTree(info.Pid, port);
                    Console.WriteLine(freed ? $"stopped (pid={info.Pid})" : "stop failed: port still open");
                    return freed ? 0 : 1;
                }
                Console.WriteLine(info.Pid == 0 ? "no server on port" : "port is not dsh, refused");
                return info.Pid == 0 ? 0 : 2;
            }
            default:
                return 1;
        }
    }

    private static int? ReadPortOverride(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--port" && int.TryParse(args[i + 1], out var p)) return p;
        }
        return null;
    }

    /// <summary>执行一次重启：识别 → 捕获 → 杀树 → 等待端口释放 → 启动 → 等待就绪。</summary>
    private static bool RestartOnce(int port)
    {
        var info = ServerController.Inspect(port);
        if (info.Pid > 0 && !info.IsDsh)
        {
            ServerController.Log(LogPath, $"重启中止：端口 {port} 被非 dsh 进程占用 (pid={info.Pid})");
            return false;
        }

        ServerController.Log(LogPath, info.Pid > 0
            ? $"开始重启：停止 pid={info.Pid} 后重新启动 (cmdline={info.CommandLine})"
            : $"端口 {port} 无运行中的服务，直接启动");

        if (info.Pid > 0)
        {
            var freed = ServerController.KillProcessTree(info.Pid, port);
            if (!freed)
            {
                ServerController.Log(LogPath, "重启失败：端口未能释放");
                return false;
            }
        }

        var started = ServerController.StartServer(info, port, AppDir, LogPath);
        // 端口已监听还不够：确认 HTTP 真的响应（避免端口被无关程序占住时误报成功）
        var ready = started && ServerController.ProbeHttp(port);
        if (!ready)
        {
            ServerController.Log(LogPath, "重启失败：服务未能在 60s 内就绪");
            return false;
        }
        ServerController.Log(LogPath, "重启成功");
        return true;
    }

    private static TrayConfig LoadConfig()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var cfg = JsonSerializer.Deserialize<TrayConfig>(File.ReadAllText(ConfigPath), JsonOpts);
                if (cfg is not null && cfg.Port is > 0 and < 65536) return cfg;
            }
        }
        catch (Exception ex)
        {
            ServerController.Log(LogPath, $"读取配置失败，使用默认值: {ex.Message}");
        }
        return new TrayConfig();
    }

    /// <summary>首次运行时在应用目录生成兜底启动脚本 start-dsh.cmd（已存在则跳过，用户可自行编辑）。</summary>
    private static void EnsureStartCmd(int port)
    {
        try
        {
            var path = Path.Combine(AppDir, "start-dsh.cmd");
            if (!File.Exists(path))
                File.WriteAllText(path, ServerController.GenerateDefaultStartCmd(port), Encoding.Default);
        }
        catch
        {
            // 应用目录不可写（如 Program Files）时静默跳过
        }
    }

    /// <summary>内嵌 favicon.png → Icon（与 dsh-launcher 相同做法）。</summary>
    private static Icon? LoadEmbeddedIcon()
    {
        try
        {
            var name = Assembly.GetExecutingAssembly().GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("favicon.png", StringComparison.OrdinalIgnoreCase));
            if (name is null) return null;
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
            if (stream is null) return null;
            using var bmp = new Bitmap(stream);
            return Icon.FromHandle(bmp.GetHicon());
        }
        catch
        {
            return null;
        }
    }

    /// <summary>打开浏览器访问 dsh Web UI。</summary>
    private static void OpenWebUi(int port)
    {
        try
        {
            Process.Start(new ProcessStartInfo($"http://127.0.0.1:{port}") { UseShellExecute = true });
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>托盘主循环（无主窗体）。</summary>
    private sealed class TrayAppContext : ApplicationContext
    {
        private readonly TrayConfig _cfg;
        private readonly string _log;
        private readonly NotifyIcon _tray;
        private readonly ToolStripMenuItem _restartItem;
        private readonly ToolStripMenuItem _exitItem;
        private bool _busy;

        public TrayAppContext(TrayConfig cfg, Icon? icon, string log, string appDir)
        {
            _cfg = cfg;
            _log = log;

            _tray = new NotifyIcon
            {
                Icon = icon ?? SystemIcons.Application,
                Text = $"DeepSeek Harness (port {cfg.Port})",
                Visible = true,
            };

            // 右键菜单：只有两项 —— 重启 / 退出
            _restartItem = new ToolStripMenuItem("重启");
            _exitItem = new ToolStripMenuItem("退出");
            var menu = new ContextMenuStrip();
            menu.Items.Add(_restartItem);
            menu.Items.Add(_exitItem);
            _tray.ContextMenuStrip = menu;

            _restartItem.Click += (_, _) => OnRestart();
            _exitItem.Click += (_, _) => OnExit();
            _tray.DoubleClick += (_, _) => OpenWebUi(cfg.Port);

            // 启动时自检：服务是否在跑
            System.Threading.Tasks.Task.Run(() =>
            {
                var info = ServerController.Inspect(cfg.Port);
                var msg = info.Pid > 0
                    ? (info.IsDsh
                        ? $"已检测到运行中的 dsh 服务 (pid={info.Pid})。"
                        : $"端口 {cfg.Port} 被其他程序占用，'重启' 将拒绝操作。")
                    : $"未检测到运行中的 dsh 服务。'重启' 将按 start-dsh.cmd 启动。";
                ServerController.Log(_log, "启动自检: " + msg);
                ShowBalloon("DeepSeek Harness", msg, ToolTipIcon.Info);
            });
        }

        private void OnRestart()
        {
            if (_busy) return;
            _busy = true;
            _restartItem.Enabled = false;
            _exitItem.Enabled = false;
            _tray.Text = $"DeepSeek Harness 重启中… (port {_cfg.Port})";

            System.Threading.Tasks.Task.Run(() =>
            {
                var ok = RestartOnce(_cfg.Port);
                BeginInvoke(() =>
                {
                    _busy = false;
                    _restartItem.Enabled = true;
                    _exitItem.Enabled = true;
                    _tray.Text = $"DeepSeek Harness (port {_cfg.Port})";
                    ShowBalloon("DeepSeek Harness",
                        ok ? "服务已重启。" : "重启失败，详见 %USERPROFILE%\\.dsh-tray.log",
                        ok ? ToolTipIcon.Info : ToolTipIcon.Error);
                });
            });
        }

        private void OnExit()
        {
            if (_busy) return;
            _busy = true;
            _exitItem.Enabled = false;

            System.Threading.Tasks.Task.Run(() =>
            {
                if (_cfg.StopServerOnExit)
                {
                    var info = ServerController.Inspect(_cfg.Port);
                    if (info.Pid > 0 && info.IsDsh)
                    {
                        ServerController.Log(_log, $"退出：停止 dsh 服务 (pid={info.Pid})");
                        ServerController.KillProcessTree(info.Pid, _cfg.Port);
                    }
                }
                BeginInvoke(() =>
                {
                    _tray.Visible = false;
                    ExitThread();
                });
            });
        }

        /// <summary>把回调切回 UI 线程执行。</summary>
        private void BeginInvoke(Action action)
        {
            try
            {
                var ctxt = WindowsFormsSynchronizationContext.Current
                    ?? new WindowsFormsSynchronizationContext();
                ctxt.Post(_ => action(), null);
            }
            catch
            {
                action();
            }
        }

        private void ShowBalloon(string title, string text, ToolTipIcon icon)
        {
            try
            {
                _tray.BalloonTipTitle = title;
                _tray.BalloonTipText = text;
                _tray.BalloonTipIcon = icon;
                _tray.ShowBalloonTip(4000);
            }
            catch
            {
                // 气泡失败不阻断
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _tray.Visible = false; _tray.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }
    }
}
