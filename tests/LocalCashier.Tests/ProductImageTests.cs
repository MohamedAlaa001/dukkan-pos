using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Dukkan.UI;
using LocalCashier.Domain;
using LocalCashier.Persistence;
using Xunit;

namespace LocalCashier.Tests;

public class ProductImageTests
{
    static string MakePng(int w, int h)
    {
        var path = Path.Combine(Path.GetTempPath(), "img-" + Guid.NewGuid().ToString("N") + ".png");
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen()) dc.DrawRectangle(Brushes.OrangeRed, null, new Rect(0, 0, w, h));
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
        return path;
    }

    [Fact]
    public void Picture_is_shrunk_stored_in_the_database_and_survives_restart()
    {
        using var f = new PosFixture();
        var styles = new StyleCatalogService(f.Db, f.Clock);
        var id = styles.CreateStyle(f.Owner, new StyleInput("Tee", null, "TShirts", "TShirt", 250m, 100m), [new VariantInput("M", "Black", "أسود", "#111111")]);
        var path = MakePng(2000, 1000);
        try
        {
            var bytes = Sta.Run(() => ProductImages.Prepare(path));
            Assert.InRange(bytes.Length, 100, StyleCatalogService.MaxImageBytes);
            styles.SetImage(f.Owner, id, bytes);
            Assert.True(styles.List().First(s => s.Id == id).HasImage);

            var again = new StyleCatalogService(new PosDatabase(f.Db.Path), f.Clock).GetImage(id)!;
            Assert.Equal(bytes, again);
            var (w, h) = Sta.Run(() => { var b = ProductImages.Decode(again) as BitmapSource; return (b!.PixelWidth, b.PixelHeight); });
            Assert.Equal(480, w);            // long side limited to 480 px, aspect kept
            Assert.Equal(240, h);

            styles.SetImage(f.Owner, id, null);
            Assert.False(styles.List().First(s => s.Id == id).HasImage);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Only_the_owner_can_set_pictures_and_only_real_images_are_accepted()
    {
        using var f = new PosFixture();
        var styles = new StyleCatalogService(f.Db, f.Clock);
        var id = styles.CreateStyle(f.Owner, new StyleInput("Tee", null, "TShirts", "TShirt", 250m, 100m), [new VariantInput("M", "Black", "أسود", "#111111")]);
        Assert.Equal(PosError.PermissionDenied, Assert.Throws<PosException>(() => styles.SetImage(f.Cashier, id, [0xFF, 0xD8, 0xFF, 1])).Code);
        Assert.Equal(PosError.InvalidPrice, Assert.Throws<PosException>(() => styles.SetImage(f.Owner, id, [1, 2, 3, 4, 5, 6, 7, 8, 9])).Code);
        Assert.Equal(PosError.InvalidPrice, Assert.Throws<PosException>(() => styles.SetImage(f.Owner, id, new byte[StyleCatalogService.MaxImageBytes + 1])).Code);
    }

    [Fact]
    public void Garment_thumb_shows_the_photo_instead_of_the_placeholder()
    {
        var png = MakePng(64, 64);
        try
        {
            var shown = Sta.Run(() =>
            {
                var bytes = ProductImages.Prepare(png);
                var thumb = new GarmentThumb { Width = 80, Height = 80, Image = ProductImages.Decode(bytes) };
                thumb.Measure(new Size(80, 80)); thumb.Arrange(new Rect(0, 0, 80, 80)); thumb.ApplyTemplate();
                return thumb.HasImage;
            });
            Assert.True(shown);
        }
        finally { File.Delete(png); }
    }
}
