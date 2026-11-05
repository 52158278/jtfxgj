using System.Drawing;
using System.Windows.Forms;
using HueContrast.Core;

namespace HueContrast.UI;

/// <summary>历史记录：左侧缩略图列表，右侧预览原图/标注图。</summary>
public sealed class HistoryForm : Form
{
    private readonly ListView _list = new();
    private readonly PictureBox _preview = new();
    private readonly Label _info = new();
    private readonly RadioButton _rbAnnotated = new();
    private readonly RadioButton _rbOriginal = new();
    private readonly RadioButton _rbHue = new();
    private readonly ImageList _thumbs = new() { ImageSize = new Size(160, 96), ColorDepth = ColorDepth.Depth32Bit };
    private List<HistoryMeta> _items = new();

    public HistoryForm()
    {
        Text = "历史记录";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1180, 760);
        MinimumSize = new Size(820, 520);
        BackColor = Color.FromArgb(28, 30, 34);
        ForeColor = Color.FromArgb(226, 230, 236);
        KeyPreview = true;

        BuildUi();
        Reload();
    }

    private void BuildUi()
    {
        var toolbar = new Panel
        {
            Dock = DockStyle.Top,
            Height = 46,
            BackColor = Color.FromArgb(38, 41, 46),
            Padding = new Padding(8, 7, 8, 7)
        };

        Button MakeButton(string text, EventHandler onClick)
        {
            var b = new Button
            {
                Text = text,
                AutoSize = true,
                Height = 30,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(58, 62, 70),
                ForeColor = Color.FromArgb(232, 235, 240),
                Location = new Point(8, 8)
            };
            b.FlatAppearance.BorderSize = 0;
            b.Click += onClick;
            return b;
        }

        var btnRefresh = MakeButton("刷新", (_, _) => Reload());
        var btnDelete = MakeButton("删除选中", (_, _) => DeleteSelected());
        var btnOpen = MakeButton("打开目录", (_, _) => OpenRoot());
        var btnExport = MakeButton("导出标注图", (_, _) => ExportSelected());

        btnDelete.Left = btnRefresh.Right + 8;
        btnOpen.Left = btnDelete.Right + 8;
        btnExport.Left = btnOpen.Right + 8;

        _rbAnnotated.Text = "标注图";
        _rbAnnotated.Checked = true;
        _rbAnnotated.AutoSize = true;
        _rbAnnotated.ForeColor = ForeColor;
        _rbAnnotated.Left = btnExport.Right + 20;
        _rbAnnotated.Top = 12;
        _rbAnnotated.CheckedChanged += (_, _) => ShowSelected();

        _rbOriginal.Text = "原图";
        _rbOriginal.AutoSize = true;
        _rbOriginal.ForeColor = ForeColor;
        _rbOriginal.Left = _rbAnnotated.Right + 12;
        _rbOriginal.Top = 12;
        _rbOriginal.CheckedChanged += (_, _) => ShowSelected();

        _rbHue.Text = "色相图";
        _rbHue.AutoSize = true;
        _rbHue.ForeColor = ForeColor;
        _rbHue.Left = _rbOriginal.Right + 12;
        _rbHue.Top = 12;
        _rbHue.CheckedChanged += (_, _) => ShowSelected();

        toolbar.Controls.Add(btnRefresh);
        toolbar.Controls.Add(btnDelete);
        toolbar.Controls.Add(btnOpen);
        toolbar.Controls.Add(btnExport);
        toolbar.Controls.Add(_rbAnnotated);
        toolbar.Controls.Add(_rbOriginal);
        toolbar.Controls.Add(_rbHue);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            SplitterDistance = 340,
            BackColor = Color.FromArgb(20, 22, 26),
            Panel1MinSize = 260
        };

        _list.Dock = DockStyle.Fill;
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.HideSelection = false;
        _list.LargeImageList = _thumbs;
        _list.BackColor = Color.FromArgb(26, 28, 32);
        _list.ForeColor = Color.FromArgb(226, 230, 235);
        _list.BorderStyle = BorderStyle.None;
        _list.Columns.Add("时间", 150);
        _list.SelectedIndexChanged += (_, _) => ShowSelected();

        _preview.Dock = DockStyle.Fill;
        _preview.SizeMode = PictureBoxSizeMode.Zoom;
        _preview.BackColor = Color.FromArgb(16, 18, 21);

        _info.Dock = DockStyle.Bottom;
        _info.Height = 58;
        _info.BackColor = Color.FromArgb(32, 35, 40);
        _info.ForeColor = Color.FromArgb(158, 168, 180);
        _info.Padding = new Padding(10, 6, 10, 0);

        split.Panel1.Controls.Add(_list);
        split.Panel2.Controls.Add(_preview);
        split.Panel2.Controls.Add(_info);

        Controls.Add(split);
        Controls.Add(toolbar);
    }

    public void ReloadHistory() => Reload();

    private void Reload()
    {
        foreach (Image img in _thumbs.Images) img.Dispose();
        _thumbs.Images.Clear();
        _list.Items.Clear();

        _items = HistoryStore.LoadAll();
        for (int i = 0; i < _items.Count; i++)
        {
            var m = _items[i];
            _thumbs.Images.Add(i.ToString(), MakeThumb(m));
            var item = new ListViewItem(m.Title, i);
            item.SubItems.Add($"{m.Width}×{m.Height}");
            _list.Items.Add(item);
        }

        if (_list.Items.Count > 0) _list.Items[0].Selected = true;
        else
        {
            _preview.Image = null;
            _info.Text = "暂无历史记录。按 " + AppSettings.Load().HotkeyText + " 截图试试。";
        }
    }

    private static Image MakeThumb(HistoryMeta m)
    {
        try
        {
            if (File.Exists(m.AnnotatedPath))
            {
                using var fs = new FileStream(m.AnnotatedPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var src = Image.FromStream(fs);
                var bmp = new Bitmap(160, 96);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                    g.Clear(Color.FromArgb(26, 28, 32));
                    double s = Math.Min(160.0 / src.Width, 96.0 / src.Height);
                    int w = Math.Max(1, (int)(src.Width * s));
                    int h = Math.Max(1, (int)(src.Height * s));
                    g.DrawImage(src, (160 - w) / 2, (96 - h) / 2, w, h);
                }
                return bmp;
            }
        }
        catch
        {
            // 缩略图失败时用空白占位
        }
        var blank = new Bitmap(160, 96);
        using (var g = Graphics.FromImage(blank)) g.Clear(Color.FromArgb(40, 44, 50));
        return blank;
    }

    private HistoryMeta SelectedMeta()
    {
        if (_list.SelectedIndices.Count == 0) return null;
        int i = _list.SelectedIndices[0];
        return i >= 0 && i < _items.Count ? _items[i] : null;
    }

    private void ShowSelected()
    {
        var m = SelectedMeta();
        if (m == null) return;

        string path = _rbOriginal.Checked ? m.OriginalPath : _rbHue.Checked ? m.HuePath : m.AnnotatedPath;
        _preview.Image?.Dispose();
        _preview.Image = null;
        try
        {
            if (File.Exists(path))
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var src = Image.FromStream(fs);
                _preview.Image = new Bitmap(src);
            }
        }
        catch
        {
            _preview.Image = null;
        }

        var lines = new List<string> { $"{m.Title}　{m.Width}×{m.Height}　{m.Profile ?? ""}　{Path.GetFileName(m.Dir)}" };
        foreach (var r in m.Relations.Take(3))
            lines.Add($"{r.BId} 在 {r.AId}：ΔL {r.DeltaL:0}　WCAG {r.Wcag:0.0}");
        if (m.Relations.Count == 0)
            foreach (var g in m.Groups.Take(3))
                lines.Add($"{g.Name} {g.Dark} : {g.Bright}（差{g.Diff}·{g.Level}）");
        _info.Text = string.Join("    ", lines);
    }

    private void DeleteSelected()
    {
        var m = SelectedMeta();
        if (m == null) return;
        if (MessageBox.Show(this, $"删除 {m.Title} 这条记录？", "确认删除",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        HistoryStore.Delete(m);
        Reload();
    }

    private void ExportSelected()
    {
        var m = SelectedMeta();
        if (m == null) return;
        using var dlg = new SaveFileDialog
        {
            Filter = "PNG 图片|*.png",
            FileName = $"hue-{m.Created:yyyyMMdd-HHmmss}.png"
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            File.Copy(m.AnnotatedPath, dlg.FileName, true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "导出失败：" + ex.Message, "导出", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void OpenRoot()
    {
        try
        {
            Directory.CreateDirectory(HistoryStore.Root);
            System.Diagnostics.Process.Start("explorer.exe", HistoryStore.Root);
        }
        catch { /* 忽略 */ }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _preview.Image?.Dispose();
        _preview.Image = null;
        foreach (Image img in _thumbs.Images) img.Dispose();
        base.OnFormClosed(e);
    }
}
