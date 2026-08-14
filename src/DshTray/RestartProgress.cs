using System;
using System.IO;
using System.Linq;

namespace DshTray;

/// <summary>
/// 重启进度记录：独立 worker 进程把阶段/结果写进 %USERPROFILE%\.dsh-tray-restart.log，
/// 托盘进度窗口轮询该文件显示，解耦「执行」与「展示」（托盘崩溃也不中断重启）。
/// </summary>
internal static class RestartProgress
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh-tray-restart.log");

    private static readonly object Gate = new();

    public static string LogFilePath => LogPath;

    public static void Write(string marker)
    {
        lock (Gate)
        {
            try
            {
                File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss}] {marker}\n");
            }
            catch
            {
                // 记录失败不阻断重启
            }
        }
    }

    public static void Stage(int percent, string text) => Write($"[stage] {percent} {text}");
    public static void Ok(string text) => Write($"[ok] {text}");
    public static void Fail(string text) => Write($"[fail] {text}");

    /// <summary>清空进度文件（每次重启开始时调用，避免进度窗显示上一次的旧结果）。</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            try
            {
                if (File.Exists(LogPath)) File.Delete(LogPath);
            }
            catch
            {
                // 忽略
            }
        }
    }

    /// <summary>最后一行是否是 [ok]/[fail]（即重启已结束）。</summary>
    public static bool IsFinished()
    {
        lock (Gate)
        {
            try
            {
                var lines = File.ReadAllLines(LogPath);
                if (lines.Length == 0) return false;
                var last = lines[^1];
                return last.Contains("[ok]", StringComparison.Ordinal) || last.Contains("[fail]", StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>解析进度文件：取最后一个 [stage] 的百分比/文案，以及最终 [ok]/[fail] 详情。</summary>
    public static (int Percent, string Stage, bool Done, bool Ok, string Detail) Snapshot()
    {
        lock (Gate)
        {
            var percent = 0;
            var stage = "准备中…";
            var done = false;
            var ok = false;
            var detail = "";
            try
            {
                foreach (var line in File.ReadAllLines(LogPath))
                {
                    if (line.Contains("[stage]", StringComparison.Ordinal))
                    {
                        var rest = line.Substring(line.IndexOf("[stage]", StringComparison.Ordinal) + 7).Trim();
                        var sp = rest.IndexOf(' ');
                        if (sp > 0 && int.TryParse(rest[..sp], out var p)) percent = p;
                        stage = sp > 0 ? rest[(sp + 1)..] : rest;
                    }
                    else if (line.Contains("[ok]", StringComparison.Ordinal))
                    {
                        done = true; ok = true; detail = line.Substring(line.IndexOf("[ok]", StringComparison.Ordinal) + 5).Trim();
                    }
                    else if (line.Contains("[fail]", StringComparison.Ordinal))
                    {
                        done = true; ok = false; detail = line.Substring(line.IndexOf("[fail]", StringComparison.Ordinal) + 6).Trim();
                    }
                }
            }
            catch
            {
                // 读不到就保持初始值
            }
            return (percent, stage, done, ok, detail);
        }
    }

    /// <summary>读取服务器日志尾部（失败详情用）。</summary>
    public static string ServerLogTail(int lines = 8)
    {
        var serverLog = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh-tray-server.log");
        try
        {
            if (!File.Exists(serverLog)) return "(无 .dsh-tray-server.log)";
            var all = File.ReadAllLines(serverLog);
            return string.Join('\n', all.Skip(Math.Max(0, all.Length - lines)));
        }
        catch (Exception ex)
        {
            return "(读取服务器日志失败: " + ex.Message + ")";
        }
    }
}
