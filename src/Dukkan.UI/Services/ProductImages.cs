using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LocalCashier.Persistence;

namespace Dukkan.UI;

/// <summary>Product photos: shrink a picked file to a small JPEG (stored in the database) and cache decoded images for lists.</summary>
public static class ProductImages
{
    const int MaxSide = 480;
    static StyleCatalogService? _styles;
    static readonly Dictionary<long, ImageSource?> Cache = [];

    public static void Configure(StyleCatalogService styles) { _styles = styles; Cache.Clear(); }

    /// <summary>Reads any common image file, scales it so the long side is at most 480 px on white, and returns JPEG bytes.</summary>
    public static byte[] Prepare(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException(path);
        if (info.Length > 30L * 1024 * 1024) throw new InvalidDataException("The picture file is too large.");

        BitmapSource src;
        using (var fs = File.OpenRead(path))
        {
            var frame = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            src = frame;
        }
        var scale = Math.Min(1.0, (double)MaxSide / Math.Max(src.PixelWidth, src.PixelHeight));
        var w = Math.Max(1, (int)Math.Round(src.PixelWidth * scale));
        var h = Math.Max(1, (int)Math.Round(src.PixelHeight * scale));

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));
            dc.DrawImage(src, new Rect(0, 0, w, h));
        }
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);

        for (var quality = 88; quality >= 40; quality -= 16)
        {
            var enc = new JpegBitmapEncoder { QualityLevel = quality };
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using var ms = new MemoryStream();
            enc.Save(ms);
            if (ms.Length <= StyleCatalogService.MaxImageBytes) return ms.ToArray();
        }
        throw new InvalidDataException("The picture could not be reduced enough.");
    }

    public static ImageSource? Decode(byte[]? bytes)
    {
        if (bytes is not { Length: > 0 }) return null;
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.DecodePixelWidth = MaxSide;
        bmp.StreamSource = new MemoryStream(bytes);
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    public static ImageSource? Get(long styleId)
    {
        if (_styles is null) return null;
        if (Cache.TryGetValue(styleId, out var hit)) return hit;
        ImageSource? img = null;
        try { img = Decode(_styles.GetImage(styleId)); } catch { /* a damaged picture simply falls back to the placeholder */ }
        return Cache[styleId] = img;
    }

    public static void Invalidate(long styleId) => Cache.Remove(styleId);
}
