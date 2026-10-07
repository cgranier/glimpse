using System.Security.Cryptography;
using System.Text;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Glimpse.Core;

/// <summary>
/// Small PNG thumbnails in %LOCALAPPDATA%\Glimpse\thumbs, keyed by path + size + mtime so an edited
/// file gets a fresh one. Used where a consumer (the Command Palette) loads images itself and would
/// otherwise decode full 4K screenshots.
/// </summary>
public static class Thumbnails
{
    public const int Size = 256;
    public static string Dir { get; } = Path.Combine(GlimpseConfig.DataDir, "thumbs");

    public static async Task<string?> GetAsync(string path, CancellationToken ct = default)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;

            var key = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes($"{path.ToLowerInvariant()}|{info.Length}|{info.LastWriteTimeUtc.Ticks}")));
            var thumb = Path.Combine(Dir, key[..2], key + ".png");
            if (File.Exists(thumb)) return thumb;

            Directory.CreateDirectory(Path.GetDirectoryName(thumb)!);
            var file = await StorageFile.GetFileFromPathAsync(path).AsTask(ct);
            using IRandomAccessStream input = await file.OpenAsync(FileAccessMode.Read).AsTask(ct);
            var decoder = await BitmapDecoder.CreateAsync(input).AsTask(ct);

            double w = decoder.OrientedPixelWidth, h = decoder.OrientedPixelHeight;
            var scale = Math.Min(1.0, Size / Math.Max(w, h));
            var transform = new BitmapTransform
            {
                ScaledWidth = (uint)Math.Max(1, w * scale),
                ScaledHeight = (uint)Math.Max(1, h * scale),
                InterpolationMode = BitmapInterpolationMode.Fant,
            };
            using var bitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
                ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage).AsTask(ct);

            // Write to a temp name and move, so a half-written thumbnail is never served.
            var temp = thumb + ".tmp";
            File.Create(temp).Dispose();
            var tempFile = await StorageFile.GetFileFromPathAsync(temp).AsTask(ct);
            using (var output = await tempFile.OpenAsync(FileAccessMode.ReadWrite).AsTask(ct))
            {
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output).AsTask(ct);
                encoder.SetSoftwareBitmap(bitmap);
                await encoder.FlushAsync().AsTask(ct);
            }
            File.Move(temp, thumb, overwrite: true);
            return thumb;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }
}
