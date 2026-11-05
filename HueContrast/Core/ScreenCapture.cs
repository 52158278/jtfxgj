using System.Drawing;
using System.Drawing.Imaging;

namespace HueContrast.Core;

public static class ScreenCapture
{
    /// <summary>抓取屏幕指定区域（物理像素坐标）。</summary>
    public static Bitmap Capture(Rectangle region)
    {
        Rectangle vs = SystemInformation.VirtualScreen;
        region.Intersect(vs);
        if (region.Width <= 0 || region.Height <= 0) return null;

        var bmp = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(region.X, region.Y, 0, 0, region.Size, CopyPixelOperation.SourceCopy);
        }
        return bmp;
    }

    public static Bitmap CaptureVirtualScreen()
    {
        return Capture(SystemInformation.VirtualScreen);
    }
}
