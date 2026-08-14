using System;
using System.Drawing;
using System.Windows.Forms;

namespace DshTray;

/// <summary>
/// 重启进度窗口：进度条 + 阶段文案 + 失败详情。轮询 .dsh-tray-restart.log，
/// 实时反映独立 worker 的进度；[ok] 后自动关闭，[fail] 停留显示原因与服务器日志尾部。
/// </summary>
internal sealed class RestartProgressForm : Form
{
    private readonly ProgressBar _bar;
    private readonly Label _stage;
    private readonly TextBox _detail;
    private readonly Button _closeButton;
    private readonly System.Windows.Forms.Timer _timer;
    private bool _settled;

    public RestartProgressForm(int port)
    {
        Text = $"正在重启 DeepSeek Harness (port {port})";
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(480, 210);
        ShowInTaskbar = false;
        TopMost = true;

        _stage = new Label
        {
            AutoSize = false,
            Location = new Point(14, 12),
            Size = new Size(452, 20),
            Font = new Font(Font.FontFamily, 9.5f, FontStyle.Bold),
            Text = "准备中…",
        };

        _bar = new ProgressBar
        {
            Location = new Point(14, 38),
            Size = new Size(452, 18),
            Minimum = 0,
            Maximum = 100,
            Value = 0,
        };

        _detail = new TextBox
        {
            Location = new Point(14, 64),
            Size = new Size(452, 110),
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Font = new Font("Consolas", 8.5f),
            Visible = false,
        };

        _closeButton = new Button
        {
            Text = "关闭",
            Location = new Point(396, 182),
            Size = new Size(70, 24),
            Visible = false,
        };
        _closeButton.Click += (_, _) => Close();

        Controls.Add(_stage);
        Controls.Add(_bar);
        Controls.Add(_detail);
        Controls.Add(_closeButton);

        _timer = new System.Windows.Forms.Timer { Interval = 400 };
        _timer.Tick += (_, _) => Poll();
        _timer.Start();
    }

    /// <summary>轮询进度文件并刷新 UI（成功自动关闭，失败显示详情）。</summary>
    private void Poll()
    {
        if (_settled) return;
        var (percent, stage, done, ok, detail) = RestartProgress.Snapshot();
        _stage.Text = stage;
        _bar.Value = Math.Clamp(percent, 0, 100);

        if (!done) return;

        _settled = true;
        _timer.Stop();
        if (ok)
        {
            _stage.Text = "重启成功：服务已就绪";
            _bar.Value = 100;
            // 短暂停留后自动关闭
            var closeTimer = new System.Windows.Forms.Timer { Interval = 1500 };
            closeTimer.Tick += (_, _) => { closeTimer.Stop(); Close(); };
            closeTimer.Start();
        }
        else
        {
            _stage.Text = "重启失败";
            _detail.Visible = true;
            _closeButton.Visible = true;
            var serverTail = RestartProgress.ServerLogTail(10);
            _detail.Text = $"{detail}\r\n\r\n—— 服务器输出（~/.dsh-tray-server.log 末尾）——\r\n{serverTail}";
            _bar.Value = 0;
        }
    }

    /// <summary>外部（菜单恢复逻辑）在结束时调用一次，确保窗口按最终状态收尾。</summary>
    public void Settle()
    {
        if (_settled) return;
        Poll();
    }
}
