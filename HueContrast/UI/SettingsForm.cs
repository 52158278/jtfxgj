using System.Drawing;
using System.Windows.Forms;
using HueContrast.Core;

namespace HueContrast.UI;

public sealed class SettingsForm : Form
{
    private readonly AppSettings _settings;
    private readonly TextBox _hotkeyBox = new();
    private HotkeySpec _pending;

    public SettingsForm(AppSettings settings)
    {
        _settings = settings;
        _pending = settings.GetHotkey();

        Text = "设置";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(520, 500);
        BackColor = Color.FromArgb(30, 32, 37);
        ForeColor = Color.FromArgb(228, 232, 238);

        BuildUi();
    }

    private void BuildUi()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Padding = new Padding(18, 16, 18, 10),
            AutoSize = false
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        Label MakeLabel(string text) => new()
        {
            Text = text,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 10, 10, 6),
            ForeColor = Color.FromArgb(214, 219, 226)
        };

        _hotkeyBox.SetBounds(0, 0, 220, 26);
        _hotkeyBox.Text = _pending.ToString();
        _hotkeyBox.ReadOnly = true;
        _hotkeyBox.BackColor = Color.FromArgb(44, 48, 55);
        _hotkeyBox.ForeColor = Color.FromArgb(255, 224, 130);
        _hotkeyBox.BorderStyle = BorderStyle.FixedSingle;
        _hotkeyBox.KeyDown += HotkeyBox_KeyDown;
        _hotkeyBox.Margin = new Padding(0, 6, 0, 6);

        var hotkeyHint = new Label
        {
            Text = "点击输入框后直接按下组合键（需含 Ctrl / Alt / Shift / Win 之一）",
            AutoSize = true,
            MaximumSize = new Size(300, 0),
            ForeColor = Color.FromArgb(140, 150, 162),
            Margin = new Padding(0, 0, 0, 8)
        };

        var numAchromatic = MakeNumber(_settings.AchromaticSat, 0, 60, 1);
        var numShare = MakeNumber((decimal)_settings.MinSharePercent, 0.1m, 30m, 0.1m);
        var numWidth = MakeNumber(_settings.MaxAnalysisWidth, 640, 4096, 64);
        var numRegions = MakeNumber(_settings.MaxRegionsPerGroup, 0, 40, 1);
        var numArea = MakeNumber((decimal)_settings.MinRegionAreaPercent, 0.02m, 5m, 0.01m);
        var numElWidth = MakeNumber(_settings.ElementMaxWidth, 640, 4096, 64);
        var numSeed = MakeNumber(_settings.ElementSeedColors, 6, 32, 1);

        var cmbProfile = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 180,
            Margin = new Padding(0, 6, 10, 6),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(44, 48, 55),
            ForeColor = Color.FromArgb(232, 236, 242)
        };
        cmbProfile.Items.AddRange(new object[] { "自动判断", "界面 UI", "插画" });
        cmbProfile.SelectedIndex = _settings.ElementProfile switch
        {
            "ui" => 1,
            "illustration" => 2,
            _ => 0
        };

        var btnReset = new Button
        {
            Text = "恢复默认热键",
            AutoSize = true,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(58, 62, 70),
            ForeColor = Color.FromArgb(232, 235, 240),
            Margin = new Padding(0, 0, 0, 8)
        };
        btnReset.FlatAppearance.BorderSize = 0;
        btnReset.Click += (_, _) =>
        {
            _pending = HotkeySpec.Default();
            _hotkeyBox.Text = _pending.ToString();
        };

        panel.Controls.Add(MakeLabel("截图热键"), 0, 0);
        var hotkeyStack = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, Margin = new Padding(0) };
        hotkeyStack.Controls.Add(_hotkeyBox);
        hotkeyStack.Controls.Add(btnReset);
        hotkeyStack.Controls.Add(hotkeyHint);
        panel.Controls.Add(hotkeyStack, 1, 0);

        panel.Controls.Add(MakeLabel("无彩色饱和度阈值"), 0, 1);
        panel.Controls.Add(Wrap(numAchromatic, "(0-100)，低于此值视为黑/白/灰"), 1, 1);

        panel.Controls.Add(MakeLabel("参与标注的最小占比"), 0, 2);
        panel.Controls.Add(Wrap(numShare, "(%)，太小众的色相不标注"), 1, 2);

        panel.Controls.Add(MakeLabel("分析图最大宽度"), 0, 3);
        panel.Controls.Add(Wrap(numWidth, "(px)，越大越准、越慢"), 1, 3);

        panel.Controls.Add(MakeLabel("每组区域框上限"), 0, 4);
        panel.Controls.Add(Wrap(numRegions, "(个)，0 表示不画区域框"), 1, 4);

        panel.Controls.Add(MakeLabel("区域最小面积"), 0, 5);
        panel.Controls.Add(Wrap(numArea, "(%)，小于该面积的色块不框"), 1, 5);

        panel.Controls.Add(MakeLabel("元素分析预设"), 0, 6);
        panel.Controls.Add(Wrap(cmbProfile, "自动判断适合大多数截图；界面/插画可强制指定"), 1, 6);

        panel.Controls.Add(MakeLabel("元素分析最大宽度"), 0, 7);
        panel.Controls.Add(Wrap(numElWidth, "(px)，越大越准、越慢"), 1, 7);

        panel.Controls.Add(MakeLabel("主色数量"), 0, 8);
        panel.Controls.Add(Wrap(numSeed, "(6-32)，越多分割越细"), 1, 8);

        var bottom = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 54,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(0, 10, 18, 10),
            BackColor = Color.FromArgb(34, 37, 43)
        };

        var btnOk = new Button
        {
            Text = "保存",
            Width = 96,
            Height = 30,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(214, 108, 50),
            ForeColor = Color.White,
            Margin = new Padding(8, 0, 0, 0)
        };
        btnOk.FlatAppearance.BorderSize = 0;
        btnOk.Click += (_, _) =>
        {
            ApplyValues(numAchromatic, numShare, numWidth, numRegions, numArea);
            ApplyElementValues(cmbProfile, numElWidth, numSeed);
            _settings.Save();
            DialogResult = DialogResult.OK;
            Close();
        };

        var btnCancel = new Button
        {
            Text = "取消",
            Width = 96,
            Height = 30,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(58, 62, 70),
            ForeColor = Color.FromArgb(232, 235, 240)
        };
        btnCancel.FlatAppearance.BorderSize = 0;
        btnCancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };

        bottom.Controls.Add(btnOk);
        bottom.Controls.Add(btnCancel);

        Controls.Add(panel);
        Controls.Add(bottom);
    }

    private static NumericUpDown MakeNumber(decimal value, decimal min, decimal max, decimal step)
    {
        var n = new NumericUpDown
        {
            Minimum = min,
            Maximum = max,
            Increment = step,
            DecimalPlaces = step < 1m ? (step < 0.1m ? 2 : 1) : 0,
            Value = Math.Min(max, Math.Max(min, value)),
            Width = 120,
            Margin = new Padding(0, 6, 10, 6),
            BackColor = Color.FromArgb(44, 48, 55),
            ForeColor = Color.FromArgb(232, 236, 242),
            BorderStyle = BorderStyle.FixedSingle
        };
        return n;
    }

    private static FlowLayoutPanel Wrap(Control control, string hint)
    {
        var hintLabel = new Label
        {
            Text = hint,
            AutoSize = true,
            ForeColor = Color.FromArgb(140, 150, 162),
            Margin = new Padding(0, 13, 0, 6)
        };
        var p = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0) };
        p.Controls.Add(control);
        p.Controls.Add(hintLabel);
        return p;
    }

    private void HotkeyBox_KeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        e.SuppressKeyPress = true;

        Keys key = e.KeyCode;
        if (key is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin)
            return;

        bool ctrl = e.Control, alt = e.Alt, shift = e.Shift;
        bool win = key is Keys.LWin or Keys.RWin;
        if (!ctrl && !alt && !shift && !win)
        {
            MessageBox.Show(this, "请至少搭配 Ctrl、Alt、Shift 或 Win 中的一个修饰键，避免与普通输入冲突。",
                "热键设置", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _pending = new HotkeySpec { Key = key, Ctrl = ctrl, Alt = alt, Shift = shift, Win = win };
        _hotkeyBox.Text = _pending.ToString();
    }

    private void ApplyValues(NumericUpDown achromatic, NumericUpDown share, NumericUpDown width, NumericUpDown regions, NumericUpDown area)
    {
        _settings.HotkeyText = _pending.ToString();
        _settings.AchromaticSat = (int)achromatic.Value;
        _settings.MinSharePercent = (double)share.Value;
        _settings.MaxAnalysisWidth = (int)width.Value;
        _settings.MaxRegionsPerGroup = (int)regions.Value;
        _settings.MinRegionAreaPercent = (double)area.Value;
    }

    private void ApplyElementValues(ComboBox profile, NumericUpDown width, NumericUpDown seed)
    {
        _settings.ElementProfile = profile.SelectedIndex switch
        {
            1 => "ui",
            2 => "illustration",
            _ => "auto"
        };
        _settings.ElementMaxWidth = (int)width.Value;
        _settings.ElementSeedColors = (int)seed.Value;
    }
}
