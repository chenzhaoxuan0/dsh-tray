using System;
using System.Drawing;
using System.Windows.Forms;

namespace DshTray;

/// <summary>
/// 重启进度窗口：阶段文案 + 进度条 + 实时明细转写。
/// - 进度条只在 [ok]/[fail] 落盘后到 100（成功）/0（失败），不提前到 100；
/// - 明细区常显 .dsh-tray-restart.log 的全部行（阶段 + [info] 启动尝试/pid/退出等），
///   完成后追加 .dsh-tray-server.log 末尾若干行；成功也不自动关闭，可随时点「关闭」查看。
/// </summary>
internal sealed class RestartProgressForm : Form
{
    private readonly ProgressBar _bar;
    private readonly Label _stage;
    private readonly TextBox _detail;
    private readonly Button _closeButton;
    private readonly System.Windows.Forms.Timer _timer;
    private bool _settled;
    private string _lastTranscript = "";

    public RestartProgressForm(int port)
    {
        Text = $"正在重启 DeepSeek Harness (port {port})";
        FormBorderStyle = FormBorderStyle.SizableToolWindow;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(640, 430);
        MinimumSize = new Size(480, 300);
        ShowInTaskbar = false;
        TopMost = true;

        _stage = new Label
        {
            AutoSize = false,
            Location = new Point(14, 12),
            Size = new Size(612, 20),
            Font = new Font(Font.FontFamily, 9.5f, FontStyle.Bold),
            Text = "准备中…",
        };

        _bar = new ProgressBar
        {
            Location = new Point(14, 38),
            Size = new Size(612, 18),
            Minimum = 0,
            Maximum = 100,
            Value = 0,
        };

        _detail = new TextBox
        {
            Location = new Point(14, 64),
            Size = new Size(612, 330),
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Font = new Font("Consolas", 8.5f),
            WordWrap = false,
        };

        _closeButton = new Button
        {
            Text = "关闭",
            Location = new Point(556, 402),
            Size = new Size(70, 24),
        };
        _closeButton.Click += (_, _) => Close();

        Controls.Add(_stage);
        Controls.Add(_bar);
        Controls.Add(_detail);
        Controls.Add(_closeButton);
        Resize += (_, _) => LayoutControls();
        LayoutControls();

        _timer = new System.Windows.Forms.Timer { Interval = 400 };
        _timer.Tick += (_, _) => Poll();
        _timer.Start();
    }

    /// <summary>按窗口尺寸重排控件（可拖拽缩放）。</summary>
    private void LayoutControls()
    {
        var w = ClientSize.Width;
        var h = ClientSize.Height;
        _stage.Width = w - 28;
        _bar.Width = w - 28;
        _detail.Width = w - 28;
        _detail.Height = Math.Max(120, h - 64 - 12 - 34);
        _closeButton.Location = new Point(w - 28 - 70, h - 30);
    }

    /// <summary>轮询进度文件并刷新 UI：进度条 + 实时明细转写；[ok]/[fail] 后定格，不自动关闭。</summary>
    private void Poll()
    {
        if (_settled) return;
        var (percent, stage, done, ok, detail) = RestartProgress.Snapshot();
        _stage.Text = stage;
        _bar.Value = Math.Clamp(percent, 0, 100);

        // 实时明细：进度文件新增的行追加进明细区
        var transcript = RestartProgress.Transcript();
        if (transcript != _lastTranscript)
        {
            _lastTranscript = transcript;
            _detail.Text = transcript;
            _detail.SelectionStart = _detail.TextLength;
            _detail.ScrollToCaret();
        }

        if (!done) return;

        _settled = true;
        _timer.Stop();
        if (ok)
        {
            _stage.Text = "重启成功：服务已就绪";
            _bar.Value = 100;
        }
        else
        {
            _stage.Text = "重启失败";
            _bar.Value = 0;
            var serverTail = RestartProgress.ServerLogTail(10);
            if (serverTail.Length > 0)
            {
                _detail.Text = _lastTranscript
                    + "\r\n\r\n—— 服务器输出（~/.dsh-tray-server.log 末尾）——\r\n"
                    + serverTail;
                _detail.SelectionStart = _detail.TextLength;
                _detail.ScrollToCaret();
            }
        }
        // 完成不自动关闭：明细留给你查看
    }

    /// <summary>外部（菜单恢复逻辑）在结束时调用一次，确保窗口按最终状态收尾。</summary>
    public void Settle()
    {
        if (_settled) return;
        Poll();
    }
}
