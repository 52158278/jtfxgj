using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;
using HueContrast.Core;

namespace HueContrast.UI;

/// <summary>
/// 结果窗：左侧为标注图与交互探针，右侧为元素/关系数据，另有色相分布页签。
/// 提供浏览拾取、双框对比、明度剖面、点选取色四种手动工具，弥补自动识别的误差。
/// </summary>
public sealed class ResultForm : Form
{
    private readonly Bitmap _original;
    private readonly ElementAnalysisResult _elements;
    private readonly AnalysisResult _hue;
    private readonly string _historyDir;

    private Bitmap _view;
    private Bitmap _hueView;
    private int _highlightId = -1;
    private bool _suppressSelection;

    private readonly ManualProbe _probe = new();
    private List<ProbePoint> _profile;

    private readonly ProbeCanvas _canvas = new();
    private readonly Panel _canvasHost = new();
    private readonly PictureBox _huePic = new();
    private readonly ListView _relList = new();
    private readonly ListView _elList = new();
    private readonly ListView _hueList = new();
    private readonly TextBox _log = new();
    private readonly Label _stats = new();
    private readonly Label _status = new();

    private readonly CheckBox _chkElements = new();
    private readonly CheckBox _chkRelations = new();
    private readonly CheckBox _chkLabels = new();
    private readonly CheckBox _chkActual = new();
    private readonly ComboBox _modeBox = new();
    private readonly TabControl _tabs = new();

    public ResultForm(Bitmap original, ElementAnalysisResult elements, AnalysisResult hue, string historyDir)
    {
        _original = original;
        _elements = elements;
        _hue = hue;
        _historyDir = historyDir;

        Text = "元素明度关系分析";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1340, 900);
        MinimumSize = new Size(1000, 640);
        BackColor = Color.FromArgb(28, 30, 34);
        ForeColor = Color.FromArgb(225, 228, 232);
        KeyPreview = true;

        BuildUi();
        FillData();
        ReRender();
        UpdateStats();
    }

    private void BuildUi()
    {
        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 46,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(8, 7, 8, 7),
            BackColor = Color.FromArgb(38, 41, 46)
        };

        Button MakeButton(string text, EventHandler onClick, int right = 8)
        {
            var b = new Button
            {
                Text = text,
                AutoSize = true,
                Height = 30,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(58, 62, 70),
                ForeColor = Color.FromArgb(232, 235, 240),
                Margin = new Padding(0, 0, right, 0)
            };
            b.FlatAppearance.BorderSize = 0;
            b.Click += onClick;
            return b;
        }

        _chkElements.Text = "元素框";
        _chkRelations.Text = "关系线";
        _chkLabels.Text = "编号";
        _chkActual.Text = "1:1";
        foreach (var c in new CheckBox[] { _chkElements, _chkRelations, _chkLabels })
        {
            c.AutoSize = true;
            c.ForeColor = ForeColor;
            c.Checked = true;
            c.Margin = new Padding(6, 6, 10, 0);
            c.CheckedChanged += (_, _) => ReRender();
        }
        _chkElements.Margin = new Padding(14, 6, 10, 0);
        _chkActual.AutoSize = true;
        _chkActual.ForeColor = ForeColor;
        _chkActual.Margin = new Padding(0, 6, 12, 0);
        _chkActual.CheckedChanged += (_, _) => { _canvas.ActualSize = _chkActual.Checked; RelayoutCanvas(); };

        _modeBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _modeBox.Width = 130;
        _modeBox.Height = 30;
        _modeBox.FlatStyle = FlatStyle.Flat;
        _modeBox.BackColor = Color.FromArgb(50, 54, 62);
        _modeBox.ForeColor = Color.FromArgb(236, 240, 246);
        _modeBox.Margin = new Padding(0, 4, 8, 0);
        _modeBox.Items.AddRange(new object[] { "浏览拾取", "双框对比", "明度剖面", "点选取色" });
        _modeBox.SelectedIndex = 0;
        _modeBox.SelectedIndexChanged += (_, _) =>
        {
            _canvas.Mode = (ProbeMode)_modeBox.SelectedIndex;
            _probe.ClearAll();
            _profile = null;
            _canvas.Probe = _probe;
            ReRender();
            SetStatus(TipForMode());
        };

        toolbar.Controls.Add(MakeButton("复制当前图", (_, _) => CopyImage()));
        toolbar.Controls.Add(MakeButton("另存为…", (_, _) => SaveAs()));
        toolbar.Controls.Add(MakeButton("打开存档目录", (_, _) => OpenFolder()));
        toolbar.Controls.Add(MakeButton("关闭", (_, _) => Close(), 18));
        toolbar.Controls.Add(_modeBox);
        toolbar.Controls.Add(_chkElements);
        toolbar.Controls.Add(_chkRelations);
        toolbar.Controls.Add(_chkLabels);
        toolbar.Controls.Add(_chkActual);

        // ---------------- 元素与关系页 ----------------
        var elementsPage = new TabPage("元素与关系") { BackColor = Color.FromArgb(26, 28, 32) };

        var split = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.FromArgb(20, 22, 26)
        };
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 420));

        _canvasHost.Dock = DockStyle.Fill;
        _canvasHost.AutoScroll = true;
        _canvasHost.BackColor = Color.FromArgb(16, 18, 21);
        _canvas.Dock = DockStyle.Fill;
        _canvas.Result = _elements;
        _canvas.Probe = _probe;
        _canvas.ElementClicked += OnElementClicked;
        _canvas.BoxChanged += OnBoxChanged;
        _canvas.ProfileChanged += OnProfileChanged;
        _canvas.PointPicked += OnPointPicked;
        _canvasHost.Controls.Add(_canvas);
        split.Controls.Add(_canvasHost, 0, 0);

        var right = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            BackColor = Color.FromArgb(32, 35, 40),
            Padding = new Padding(0, 0, 0, 0)
        };
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 38));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 62));
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 132));

        _stats.Dock = DockStyle.Fill;
        _stats.ForeColor = Color.FromArgb(158, 170, 184);
        _stats.Padding = new Padding(10, 8, 10, 4);
        _stats.Font = new Font("Microsoft YaHei UI", 9f);
        right.Controls.Add(_stats, 0, 0);

        _relList.Dock = DockStyle.Fill;
        PrepareList(_relList);
        _relList.Columns.Add("关系", 128);
        _relList.Columns.Add("ΔL", 44);
        _relList.Columns.Add("比值", 52);
        _relList.Columns.Add("WCAG", 54);
        _relList.Columns.Add("色相差", 54);
        _relList.Columns.Add("类型", 60);
        _relList.SelectedIndexChanged += (_, _) => OnRelationSelected();
        right.Controls.Add(_relList, 0, 1);

        _elList.Dock = DockStyle.Fill;
        PrepareList(_elList);
        _elList.Columns.Add("编号", 44);
        _elList.Columns.Add("类型", 52);
        _elList.Columns.Add("L50", 44);
        _elList.Columns.Add("P5~P95", 74);
        _elList.Columns.Add("色值", 66);
        _elList.Columns.Add("占比", 52);
        _elList.Columns.Add("渐变", 54);
        _elList.SelectedIndexChanged += (_, _) => OnElementListSelected();
        right.Controls.Add(_elList, 0, 2);

        _log.Dock = DockStyle.Fill;
        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.Vertical;
        _log.BackColor = Color.FromArgb(22, 24, 28);
        _log.ForeColor = Color.FromArgb(220, 226, 234);
        _log.BorderStyle = BorderStyle.FixedSingle;
        _log.Font = new Font("Consolas", 9f);
        _log.Margin = new Padding(10, 4, 10, 10);
        _log.Text = "测量日志：切换上方工具后在图上操作，结果会记录在这里。";
        right.Controls.Add(_log, 0, 3);

        split.Controls.Add(right, 1, 0);
        elementsPage.Controls.Add(split);

        // ---------------- 色相分布页 ----------------
        var huePage = new TabPage("色相分布") { BackColor = Color.FromArgb(26, 28, 32) };
        var hueSplit = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.FromArgb(20, 22, 26)
        };
        hueSplit.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        hueSplit.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 380));

        _huePic.Dock = DockStyle.Fill;
        _huePic.SizeMode = PictureBoxSizeMode.Zoom;
        _huePic.BackColor = Color.FromArgb(16, 18, 21);
        hueSplit.Controls.Add(_huePic, 0, 0);

        _hueList.Dock = DockStyle.Fill;
        PrepareList(_hueList);
        _hueList.Columns.Add("色相", 58);
        _hueList.Columns.Add("最暗:最亮", 92);
        _hueList.Columns.Add("差", 44);
        _hueList.Columns.Add("等级", 84);
        _hueList.Columns.Add("占比", 62);
        hueSplit.Controls.Add(_hueList, 1, 0);

        huePage.Controls.Add(hueSplit);

        _tabs.Dock = DockStyle.Fill;
        _tabs.Controls.Add(elementsPage);
        _tabs.Controls.Add(huePage);
        _tabs.SelectedIndexChanged += (_, _) =>
        {
            if (_tabs.SelectedIndex == 1) EnsureHueView();
        };

        _status.Dock = DockStyle.Bottom;
        _status.Height = 26;
        _status.BackColor = Color.FromArgb(22, 24, 28);
        _status.ForeColor = Color.FromArgb(140, 150, 162);
        _status.Padding = new Padding(10, 4, 10, 0);
        _status.Text = "结果已自动存入历史记录";

        Controls.Add(_tabs);
        Controls.Add(_status);
        Controls.Add(toolbar);
    }

    private static void PrepareList(ListView list)
    {
        list.View = View.Details;
        list.FullRowSelect = true;
        list.GridLines = false;
        list.HideSelection = false;
        list.MultiSelect = false;
        list.BackColor = Color.FromArgb(26, 28, 32);
        list.ForeColor = Color.FromArgb(226, 230, 235);
        list.BorderStyle = BorderStyle.None;
        list.Font = new Font("Microsoft YaHei UI", 9f);
    }

    private string TipForMode() => (ProbeMode)_modeBox.SelectedIndex switch
    {
        ProbeMode.Compare => "双框对比：直接拖拽框 A，按住 Shift 拖拽框 B。",
        ProbeMode.Profile => "明度剖面：在图上拖一条直线，查看沿线明度变化。",
        ProbeMode.Pick => "点选取色：单击任意位置，读取该点明度与色值。",
        _ => "浏览拾取：单击元素即可查看它的明度与关系。"
    };

    private void FillData()
    {
        _relList.Items.Clear();
        foreach (var rel in _elements.Relations)
        {
            var a = _elements.Find(rel.AId);
            var b = _elements.Find(rel.BId);
            if (a == null || b == null) continue;
            string kind = rel.Kind switch
            {
                RelationKind.TextOnFill => "文字在上",
                RelationKind.Contained => "包含",
                _ => "相邻"
            };
            var item = new ListViewItem(new[]
            {
                $"{b.Id}.{b.KindName} 在 {a.Id}.{a.KindName}",
                rel.DeltaL.ToString("0"),
                $"{Math.Max(1, Math.Min(a.P50, b.P50)):0} : {Math.Max(a.P50, b.P50):0}",
                rel.Wcag.ToString("0.0"),
                rel.HueDiff.ToString("0"),
                kind
            });
            item.Tag = rel;
            item.ForeColor = rel.Kind == RelationKind.TextOnFill ? Color.FromArgb(48, 214, 200)
                           : rel.Kind == RelationKind.Contained ? Color.FromArgb(150, 178, 226)
                           : Color.FromArgb(206, 206, 140);
            _relList.Items.Add(item);
        }

        _elList.Items.Clear();
        foreach (var e in _elements.Elements)
        {
            var item = new ListViewItem(new[]
            {
                e.Id.ToString(),
                e.KindName,
                e.P50.ToString("0"),
                $"{e.P5:0}~{e.P95:0}",
                e.Hex,
                $"{e.SharePercent:0.#}%",
                e.HasGradient ? "Δ" + e.GradientDeltaL.ToString("0") : "—"
            });
            item.Tag = e;
            item.ForeColor = e.FrameColor;
            _elList.Items.Add(item);
        }

        _hueList.Items.Clear();
        foreach (var g in _hue.Groups)
        {
            var item = new ListViewItem(new[]
            {
                g.Name, g.RatioText, g.Diff.ToString(), g.Level,
                $"{g.SharePercent:0.#}%"
            });
            item.ForeColor = g.FrameColor;
            _hueList.Items.Add(item);
        }
    }

    private void UpdateStats()
    {
        string er = _elements.Profile;
        _stats.Text =
            $"判型 {er}　{_elements.Width}×{_elements.Height}　分析 {_elements.AnalysisWidth}×{_elements.AnalysisHeight}　" +
            $"耗时 {_elements.ElapsedMs} ms\r\n元素 {_elements.Elements.Count} 个　关系 {_elements.Relations.Count} 条　" +
            "明度 0~100（黑=0，白=100）";
    }

    // ---------------- 渲染 ----------------

    private RenderOptions CurrentOptions(bool withProbe)
    {
        return new RenderOptions
        {
            DrawElements = _chkElements.Checked,
            DrawRelations = _chkRelations.Checked,
            DrawLabels = _chkLabels.Checked,
            HighlightId = _highlightId,
            Probe = withProbe ? _probe : null,
            ProfileLine = withProbe ? _profile : null
        };
    }

    private void ReRender()
    {
        var bmp = ElementsRenderer.Render(_original, _elements, CurrentOptions(true));
        var old = _view;
        _view = bmp;
        _canvas.Image = bmp;
        if (old != null && !ReferenceEquals(old, bmp)) old.Dispose();
        RelayoutCanvas();
    }

    private void RelayoutCanvas()
    {
        if (_chkActual.Checked)
        {
            _canvas.Dock = DockStyle.None;
            if (_view != null)
            {
                _canvas.Location = new Point(0, 0);
                _canvas.Size = _view.Size;
            }
        }
        else
        {
            _canvas.Dock = DockStyle.Fill;
        }
        _canvas.Invalidate();
    }

    private void EnsureHueView()
    {
        if (_hueView != null) return;
        _hueView = Annotator.Render(_original, _hue, true);
        _huePic.Image = _hueView;
    }

    // ---------------- 交互 ----------------

    private void OnElementClicked(int id)
    {
        if (id <= 0)
        {
            _highlightId = -1;
            SetStatus("该位置没有识别到元素。可切换“双框对比”手动测量。");
        }
        else
        {
            _highlightId = id;
            var e = _elements.Find(id);
            if (e != null)
            {
                SetStatus($"{e.DisplayName}　L {e.P50:0}（{e.P5:0}~{e.P95:0}）　{e.Hex}　占比 {e.SharePercent:0.#}%");
                AppendLog($"拾取元素 {e.Id}.{e.KindName}  明度 {e.P50:0}  范围 {e.P5:0}~{e.P95:0}  色值 {e.Hex}");
            }
        }
        SyncSelection();
        ReRender();
    }

    private void OnBoxChanged(Rectangle rect, bool isB)
    {
        if (isB) _probe.BoxB = rect;
        else _probe.BoxA = rect;

        var a = _probe.BoxA.HasValue ? Probe.Measure(_original, _probe.BoxA.Value) : null;
        var b = _probe.BoxB.HasValue ? Probe.Measure(_original, _probe.BoxB.Value) : null;
        string text = Probe.Describe(a, b);
        SetStatus(text);
        AppendLog("框选对比　" + text);
        ReRender();
    }

    private void OnProfileChanged(Point a, Point b)
    {
        _probe.ProfileStart = a;
        _probe.ProfileEnd = b;
        _profile = Probe.AlongLine(_original, a, b);
        string text = Probe.DescribeLine(_profile);
        SetStatus(text);
        AppendLog($"剖面 ({a.X},{a.Y})→({b.X},{b.Y})　{text}");
        ReRender();
    }

    private void OnPointPicked(Point p)
    {
        var pick = Probe.Pick(_original, p);
        _probe.PickAt = p;
        if (pick == null) return;
        string text = $"点 ({p.X},{p.Y})　明度 {pick.L:0}　色值 {ColorMath.Hex(pick.Color)}";
        SetStatus(text);
        AppendLog("点选　" + text);
        ReRender();
    }

    private void OnRelationSelected()
    {
        if (_suppressSelection) return;
        if (_relList.SelectedItems.Count == 0) return;
        var rel = (ElementRelation)_relList.SelectedItems[0].Tag;
        var a = _elements.Find(rel.AId);
        var b = _elements.Find(rel.BId);
        if (a == null || b == null) return;
        _highlightId = a.Id;
        SetStatus($"{b.DisplayName} 在 {a.DisplayName} 上　ΔL {rel.DeltaL:0}　WCAG {rel.Wcag:0.00}（{ColorMath.ContrastLevel(rel.Wcag)}）");
        SyncSelection();
        ReRender();
    }

    private void OnElementListSelected()
    {
        if (_suppressSelection) return;
        if (_elList.SelectedItems.Count == 0) return;
        var e = (VisualElement)_elList.SelectedItems[0].Tag;
        _highlightId = e.Id;
        SetStatus($"{e.DisplayName}　L {e.P50:0}（{e.P5:0}~{e.P95:0}）　{e.Hex}　占比 {e.SharePercent:0.#}%");
        ReRender();
    }

    private void SyncSelection()
    {
        _suppressSelection = true;
        foreach (ListViewItem item in _elList.Items)
        {
            var e = (VisualElement)item.Tag;
            if (e.Id == _highlightId)
            {
                item.Selected = true;
                item.EnsureVisible();
            }
        }
        _suppressSelection = false;
    }

    private void SetStatus(string text) => _status.Text = text;

    private void AppendLog(string text)
    {
        string line = DateTime.Now.ToString("HH:mm:ss") + "  " + text;
        _log.AppendText((_log.Text.Length > 0 ? Environment.NewLine : "") + line);
        _log.SelectionStart = _log.TextLength;
        _log.ScrollToCaret();
    }

    // ---------------- 导出 ----------------

    private Bitmap CurrentImage() => _tabs.SelectedIndex == 1 ? _hueView : _view;

    private void CopyImage()
    {
        var bmp = CurrentImage();
        if (bmp == null) return;
        Clipboard.SetImage(bmp);
        SetStatus(_tabs.SelectedIndex == 1 ? "色相分布图已复制到剪贴板" : "元素标注图已复制到剪贴板");
    }

    private void SaveAs()
    {
        var bmp = CurrentImage();
        if (bmp == null) return;
        using var dlg = new SaveFileDialog
        {
            Filter = "PNG 图片|*.png",
            FileName = $"{( _tabs.SelectedIndex == 1 ? "hue" : "elements")}-{DateTime.Now:yyyyMMdd-HHmmss}.png"
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        bmp.Save(dlg.FileName, ImageFormat.Png);
        SetStatus("已另存为 " + dlg.FileName);
    }

    private void OpenFolder()
    {
        try
        {
            if (!string.IsNullOrEmpty(_historyDir) && Directory.Exists(_historyDir))
                System.Diagnostics.Process.Start("explorer.exe", _historyDir);
        }
        catch { /* 打不开资源管理器时忽略 */ }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) Close();
        base.OnKeyDown(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _canvas.Image = null;
        _huePic.Image = null;
        _view?.Dispose();
        _hueView?.Dispose();
        _original.Dispose();
        base.OnFormClosed(e);
    }
}
