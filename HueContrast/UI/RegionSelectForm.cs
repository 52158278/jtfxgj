using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using HueContrast.Core;

namespace HueContrast.UI;

/// <summary>全屏暗化遮罩，拖拽选出截图区域；ESC 取消，松开鼠标确认。</summary>
public sealed class RegionSelectForm : Form
{
    private readonly Bitmap _screen;
    private readonly Rectangle _virtual;
    private Point _start;
    private Point _current;
    private bool _dragging;
    private Rectangle _selection;

    public Rectangle SelectedRegion { get; private set; } = Rectangle.Empty;

    public RegionSelectForm(Bitmap screen, Rectangle virtualScreen)
    {
        _screen = screen;
        _virtual = virtualScreen;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = virtualScreen;
        TopMost = true;
        ShowInTaskbar = false;
        DoubleBuffered = true;
        Cursor = Cursors.Cross;
        KeyPreview = true;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
        SetForegroundWindowSafe();
    }

    private void SetForegroundWindowSafe()
    {
        try { Activate(); } catch { /* 前台激活失败不影响使用 */ }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            DialogResult = DialogResult.Cancel;
            Close();
            return;
        }
        if (e.KeyCode == Keys.Enter && _selection.Width > 0)
        {
            Confirm();
            return;
        }
        base.OnKeyDown(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right)
        {
            DialogResult = DialogResult.Cancel;
            Close();
            return;
        }
        if (e.Button != MouseButtons.Left) return;
        _dragging = true;
        _start = e.Location;
        _current = e.Location;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!_dragging) return;
        _current = e.Location;
        _selection = MakeRect(_start, _current);
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        _selection = MakeRect(_start, e.Location);
        if (_selection.Width < 8 || _selection.Height < 8)
        {
            // 太小视为误触，回到等待拖拽状态
            _selection = Rectangle.Empty;
            Invalidate();
            return;
        }
        Confirm();
    }

    private void Confirm()
    {
        Rectangle local = _selection;
        SelectedRegion = new Rectangle(local.X + _virtual.X, local.Y + _virtual.Y, local.Width, local.Height);
        DialogResult = DialogResult.OK;
        Close();
    }

    private static Rectangle MakeRect(Point a, Point b)
    {
        return new Rectangle(
            Math.Min(a.X, b.X),
            Math.Min(a.Y, b.Y),
            Math.Abs(a.X - b.X),
            Math.Abs(a.Y - b.Y));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // 冻结的屏幕内容
        g.DrawImage(_screen, new Rectangle(0, 0, _virtual.Width, _virtual.Height));

        using (var shade = new SolidBrush(Color.FromArgb(120, 0, 0, 0)))
            g.FillRectangle(shade, new Rectangle(0, 0, _virtual.Width, _virtual.Height));

        if (_selection.Width > 0)
        {
            // 选区内部还原为原亮度
            var src = new Rectangle(
                _selection.X, _selection.Y,
                Math.Min(_selection.Width, _screen.Width - _selection.X),
                Math.Min(_selection.Height, _screen.Height - _selection.Y));
            if (src.Width > 0 && src.Height > 0)
                g.DrawImage(_screen, _selection, src, GraphicsUnit.Pixel);

            using (var pen = new Pen(Color.FromArgb(255, 214, 90), 2f))
                g.DrawRectangle(pen, _selection);

            // 尺寸提示
            string text = $"{_selection.Width} × {_selection.Height}";
            using var font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);
            var sz = g.MeasureString(text, font);
            int tx = _selection.Right + 8;
            int ty = _selection.Bottom + 6;
            if (tx + sz.Width + 12 > _virtual.Width) tx = Math.Max(4, _selection.Right - (int)sz.Width - 14);
            if (ty + sz.Height + 8 > _virtual.Height) ty = Math.Max(4, _selection.Y - (int)sz.Height - 10);
            using (var bg = new SolidBrush(Color.FromArgb(230, 18, 20, 24)))
                g.FillRectangle(bg, tx, ty, sz.Width + 12, sz.Height + 6);
            using (var tb = new SolidBrush(Color.FromArgb(255, 224, 130)))
                g.DrawString(text, font, tb, tx + 6, ty + 3);
        }
        else
        {
            // 提示条
            string tip = "拖拽框选截图区域　·　Esc 或右键取消　·　Enter 确认";
            using var font = new Font("Microsoft YaHei UI", 11f, FontStyle.Regular);
            var sz = g.MeasureString(tip, font);
            int cx = (_virtual.Width - (int)sz.Width) / 2;
            using (var bg = new SolidBrush(Color.FromArgb(235, 16, 18, 22)))
                g.FillRectangle(bg, cx - 16, 24, sz.Width + 32, sz.Height + 14);
            using (var tb = new SolidBrush(Color.FromArgb(235, 238, 242)))
                g.DrawString(tip, font, tb, cx, 31);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _screen?.Dispose();
        base.Dispose(disposing);
    }
}
