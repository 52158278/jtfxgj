using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using HueContrast.Core;

namespace HueContrast.UI;

/// <summary>结果图查看/交互画布：缩放显示，支持浏览拾取、双框对比、明度剖面、点选取色。</summary>
public sealed class ProbeCanvas : Control
{
    private Bitmap _image;
    private ElementAnalysisResult _result;
    private ManualProbe _probe = new();
    private ProbeMode _mode = ProbeMode.Browse;
    private bool _actualSize;

    private bool _dragging;
    private Point _dragStart;
    private Point _dragNow;
    private bool _dragIsB;

    public ProbeCanvas()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Color.FromArgb(16, 18, 21);
        Cursor = Cursors.Default;
    }

    public event Action<int> ElementClicked;
    public event Action<Rectangle, bool> BoxChanged;
    public event Action<Point, Point> ProfileChanged;
    public event Action<Point> PointPicked;

    public Bitmap Image
    {
        get => _image;
        set { _image = value; Invalidate(); }
    }

    public ElementAnalysisResult Result
    {
        get => _result;
        set { _result = value; Invalidate(); }
    }

    public ManualProbe Probe
    {
        get => _probe;
        set { _probe = value ?? new ManualProbe(); Invalidate(); }
    }

    public ProbeMode Mode
    {
        get => _mode;
        set
        {
            _mode = value;
            Cursor = value == ProbeMode.Browse ? Cursors.Default : Cursors.Cross;
            _dragging = false;
            Invalidate();
        }
    }

    public bool ActualSize
    {
        get => _actualSize;
        set { _actualSize = value; Invalidate(); }
    }

    private float ZoomScale
    {
        get
        {
            if (_image == null) return 1f;
            if (_actualSize) return 1f;
            float s = Math.Min(Width / (float)_image.Width, Height / (float)_image.Height);
            return s <= 0 ? 1f : s;
        }
    }

    private PointF Origin
    {
        get
        {
            if (_image == null) return PointF.Empty;
            if (_actualSize) return PointF.Empty;
            float s = ZoomScale;
            return new PointF((Width - _image.Width * s) / 2f, (Height - _image.Height * s) / 2f);
        }
    }

    private Point ToImage(Point client)
    {
        if (_image == null) return Point.Empty;
        float s = ZoomScale;
        var o = Origin;
        int x = (int)Math.Round((client.X - o.X) / s);
        int y = (int)Math.Round((client.Y - o.Y) / s);
        return new Point(
            Math.Max(0, Math.Min(_image.Width - 1, x)),
            Math.Max(0, Math.Min(_image.Height - 1, y)));
    }

    private RectangleF ToClient(Rectangle r)
    {
        float s = ZoomScale;
        var o = Origin;
        return new RectangleF(o.X + r.X * s, o.Y + r.Y * s, r.Width * s, r.Height * s);
    }

    private PointF ToClient(Point p)
    {
        float s = ZoomScale;
        var o = Origin;
        return new PointF(o.X + p.X * s, o.Y + p.Y * s);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.Clear(BackColor);
        if (_image == null) return;

        g.InterpolationMode = _actualSize
            ? InterpolationMode.NearestNeighbor
            : InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.Half;

        if (_actualSize) g.DrawImage(_image, 0, 0, _image.Width, _image.Height);
        else
        {
            var o = Origin;
            g.DrawImage(_image, o.X, o.Y, _image.Width * ZoomScale, _image.Height * ZoomScale);
        }

        // 元素框提示（画布层只画细框，重标注在渲染图里）
        if (_result != null && _mode == ProbeMode.Browse)
        {
            using var pen = new Pen(Color.FromArgb(90, 255, 255, 255), 1f);
            foreach (var el in _result.Elements)
            {
                if (el.Suppressed) continue;
                g.DrawRectangle(pen, ToClient(el.Bounds).X, ToClient(el.Bounds).Y,
                    ToClient(el.Bounds).Width, ToClient(el.Bounds).Height);
            }
        }

        // 正在拖拽的预览
        if (_dragging && _dragNow != _dragStart)
        {
            if (_mode == ProbeMode.Profile)
            {
                using var pen = new Pen(Color.FromArgb(220, 255, 220, 120), 1.5f);
                g.DrawLine(pen, ToClient(_dragStart), ToClient(_dragNow));
            }
            else
            {
                var rect = Normalize(ToImage(_dragStart), ToImage(_dragNow));
                Color c = _dragIsB ? Color.FromArgb(255, 168, 96) : Color.FromArgb(96, 200, 255);
                using var pen = new Pen(Color.FromArgb(220, c), 1.5f) { DashStyle = DashStyle.Dash };
                var r = ToClient(rect);
                g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
            }
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (_image == null || e.Button != MouseButtons.Left) return;
        if (_mode == ProbeMode.Browse || _mode == ProbeMode.Pick) return;

        _dragStart = ToImage(e.Location);
        _dragNow = _dragStart;
        _dragIsB = (ModifierKeys & Keys.Shift) == Keys.Shift && _mode == ProbeMode.Compare;
        _dragging = true;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging) return;
        _dragNow = ToImage(e.Location);
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_image == null || e.Button != MouseButtons.Left) return;
        var pt = ToImage(e.Location);

        switch (_mode)
        {
            case ProbeMode.Browse:
                PickElement(pt);
                break;
            case ProbeMode.Pick:
                PointPicked?.Invoke(pt);
                break;
            case ProbeMode.Compare:
            {
                var rect = Normalize(_dragStart, pt);
                _dragging = false;
                if (rect.Width >= 2 && rect.Height >= 2) BoxChanged?.Invoke(rect, _dragIsB);
                Invalidate();
                break;
            }
            case ProbeMode.Profile:
            {
                _dragging = false;
                if (Math.Abs(pt.X - _dragStart.X) + Math.Abs(pt.Y - _dragStart.Y) >= 3)
                    ProfileChanged?.Invoke(_dragStart, pt);
                Invalidate();
                break;
            }
        }
    }

    private void PickElement(Point pt)
    {
        if (_result == null) return;
        VisualElement hit = null;
        foreach (var el in _result.Elements)
        {
            if (el.Suppressed) continue;
            if (el.Kind == ElementKind.Background) continue;
            if (!el.Bounds.Contains(pt)) continue;
            if (hit == null || el.AreaPixels < hit.AreaPixels) hit = el;
        }
        ElementClicked?.Invoke(hit?.Id ?? 0);
    }

    private static Rectangle Normalize(Point a, Point b) =>
        new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
}
