using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Babelive;

/// <summary>
/// Builds the Babelive window/tray icon at runtime instead of shipping a
/// separate <c>.ico</c> asset. Used by:
/// <list type="bullet">
///   <item><see cref="TrayIconHost"/> — system-tray icon</item>
///   <item><see cref="MainWindow"/> — taskbar / Alt-Tab icon (the settings
///         window is the only one that shows in taskbar; lyric + API dialog
///         have <c>ShowInTaskbar=False</c>)</item>
/// </list>
/// </summary>
internal static class AppIcon
{
    /// <summary>
    /// Render a solid amber disc with a white "译" glyph and encode it as
    /// PNG inside a <see cref="WindowIcon"/>. 64×64 reads crisply on HiDPI
    /// taskbars; Windows downscales for the tray.
    /// </summary>
    public static WindowIcon Build()
    {
        const int size = 64;
        var bmp = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        using (var ctx = bmp.CreateDrawingContext())
        {
            var brush = new SolidColorBrush(Color.FromArgb(255, 0xFF, 0xB9, 0x38));
            ctx.DrawEllipse(brush, null, new Rect(2, 2, size - 4, size - 4));

            var text = new FormattedText(
                "译",
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface("Microsoft YaHei UI, Segoe UI", FontStyle.Normal, FontWeight.Bold),
                size * 0.62,
                Brushes.White);
            var origin = new Point((size - text.Width) / 2, (size - text.Height) / 2);
            ctx.DrawText(text, origin);
        }

        var ms = new MemoryStream();
        bmp.Save(ms);
        ms.Position = 0;
        return new WindowIcon(ms);
    }
}
