using System.Drawing;
using System.Windows.Forms;
using HueContrast.Core;
using HueContrast.UI;

namespace HueContrast;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)))
            return SelfTest.Run();

        using var mutex = new Mutex(true, @"Global\HueContrast.SingleInstance", out bool created);
        if (!created)
        {
            MessageBox.Show("色相明度比值分析已经在运行。\r\n用托盘图标或热键即可使用。",
                "已运行", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new TrayAppContext());
        return 0;
    }
}

internal sealed class TrayAppContext : ApplicationContext
{
    private readonly AppSettings _settings;
    private readonly NotifyIcon _tray;
    private readonly HotkeyManager _hotkey;
    private Icon _icon;
    private bool _busy;
    private HistoryForm _historyForm;

    public TrayAppContext()
    {
        _settings = AppSettings.Load();

        _icon = AppIcon.Create();
        var menu = new ContextMenuStrip();
        var miCapture = new ToolStripMenuItem("截图分析　(" + _settings.GetHotkey() + ")") { Font = new Font(menu.Font, FontStyle.Bold) };
        miCapture.Click += (_, _) => BeginCapture();
        var miHistory = new ToolStripMenuItem("历史记录…");
        miHistory.Click += (_, _) => ShowHistory();
        var miSettings = new ToolStripMenuItem("设置…");
        miSettings.Click += (_, _) => ShowSettings();
        var miExit = new ToolStripMenuItem("退出");
        miExit.Click += (_, _) => ExitApp();
        menu.Items.AddRange(new ToolStripItem[]
        {
            miCapture, miHistory, miSettings, new ToolStripSeparator(), miExit
        });

        _tray = new NotifyIcon
        {
            Icon = _icon,
            Text = "色相明度比值分析",
            Visible = true,
            ContextMenuStrip = menu
        };
        _tray.DoubleClick += (_, _) => BeginCapture();

        _hotkey = new HotkeyManager();
        ApplyHotkey(notify: true);

        _tray.ShowBalloonTip(3000, "已就绪",
            $"按 {_settings.GetHotkey()} 开始截图分析，也可双击托盘图标。",
            ToolTipIcon.Info);
    }

    private void ApplyHotkey(bool notify)
    {
        bool ok = _hotkey.Register(_settings.GetHotkey(), BeginCapture);
        if (ok || !notify) return;
        _tray.ShowBalloonTip(4000, "热键注册失败",
            $"{_settings.GetHotkey()} 可能被其他程序占用，请在“设置”里换一个组合。",
            ToolTipIcon.Warning);
    }

    private async void BeginCapture()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            Rectangle region;
            using (var screen = ScreenCapture.CaptureVirtualScreen())
            {
                if (screen == null) return;
                using var selector = new RegionSelectForm(screen, SystemInformation.VirtualScreen);
                var dlg = selector.ShowDialog();
                if (dlg != DialogResult.OK) return;
                region = selector.SelectedRegion;
            }

            if (region.Width < 8 || region.Height < 8) return;

            var shot = ScreenCapture.Capture(region);
            if (shot == null) return;
            Bitmap elementsView = null;
            Bitmap hueView = null;
            try
            {
                var hueOpt = _settings.ToOptions();
                var elOpt = _settings.ToElementOptions();
                var hue = await Task.Run(() => HueAnalyzer.Analyze(shot, hueOpt));
                var elements = await Task.Run(() => ElementDetector.Analyze(shot, elOpt));
                elementsView = await Task.Run(() => ElementsRenderer.Render(shot, elements, new RenderOptions()));
                hueView = await Task.Run(() => Annotator.Render(shot, hue, true));
                string dir = await Task.Run(() => HistoryStore.Save(shot, elementsView, hueView, elements, hue));

                var form = new ResultForm(shot, elements, hue, dir);
                form.Show();
                // 原图交给结果窗体释放
                shot = null;
            }
            finally
            {
                shot?.Dispose();
                elementsView?.Dispose();
                hueView?.Dispose();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show("截图分析失败：" + ex.Message, "出错了",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _busy = false;
        }
    }

    private void ShowHistory()
    {
        if (_historyForm == null || _historyForm.IsDisposed)
        {
            _historyForm = new HistoryForm();
            _historyForm.FormClosed += (_, _) => _historyForm = null;
            _historyForm.Show();
        }
        else
        {
            _historyForm.Activate();
        }
    }

    private void ShowSettings()
    {
        using var dlg = new SettingsForm(_settings);
        if (dlg.ShowDialog() != DialogResult.OK) return;
        ApplyHotkey(notify: true);
        _tray.ContextMenuStrip.Items[0].Text = "截图分析　(" + _settings.GetHotkey() + ")";
        if (_historyForm != null && !_historyForm.IsDisposed) _historyForm.ReloadHistory();
    }

    private void ExitApp()
    {
        _hotkey.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _icon?.Dispose();
        _icon = null;
        ExitThread();
    }
}

internal static class AppIcon
{
    public static Icon Create()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.FromArgb(24, 26, 31));
            using var bg = new SolidBrush(Color.FromArgb(24, 26, 31));
            g.FillEllipse(bg, 1, 1, 30, 30);

            var colors = new[]
            {
                Color.FromArgb(226, 74, 90),
                Color.FromArgb(232, 168, 52),
                Color.FromArgb(96, 190, 96),
                Color.FromArgb(70, 130, 220)
            };
            for (int i = 0; i < colors.Length; i++)
            {
                using var b = new SolidBrush(colors[i]);
                g.FillRectangle(b, 5 + i * 6, 8, 4, 16);
            }
            using var bar = new SolidBrush(Color.FromArgb(245, 245, 245));
            g.FillRectangle(bar, 4, 24, 24, 2);
        }

        IntPtr h = bmp.GetHicon();
        using var tmp = Icon.FromHandle(h);
        var icon = (Icon)tmp.Clone();
        DestroyIcon(h);
        return icon;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);
}
