using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CodeSwitchX.UI.Infrastructure;

/// <summary>
/// Rasterises a vector ImageSource (the app icon is a DrawingImage) into a GDI icon for the tray.
/// H.NotifyIcon's IconSource only accepts URI-backed bitmaps, so the Win32 icon is built here instead.
/// </summary>
public static class IconRenderer
{
    public static System.Drawing.Icon ToIcon(ImageSource source, int size)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawImage(source, new Rect(0, 0, size, size));
        }

        var target = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(target));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        stream.Position = 0;

        using var gdiBitmap = new System.Drawing.Bitmap(stream);
        // The handle stays alive for the lifetime of the process together with the tray icon.
        return System.Drawing.Icon.FromHandle(gdiBitmap.GetHicon());
    }
}
