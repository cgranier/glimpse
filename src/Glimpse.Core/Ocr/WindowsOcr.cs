using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Glimpse.Core.Ocr;

/// <summary>
/// Thin wrapper over the OCR engine built into Windows (Windows.Media.Ocr):
/// offline, no model downloads, uses the user's profile languages.
/// </summary>
public sealed class WindowsOcr
{
    readonly OcrEngine _engine;

    public WindowsOcr()
    {
        _engine = OcrEngine.TryCreateFromUserProfileLanguages()
            ?? throw new InvalidOperationException(
                "No OCR language pack is installed. Add one under Settings > Time & language > Language.");
    }

    public string Language => _engine.RecognizerLanguage.LanguageTag;

    public async Task<OcrPage> RecognizeAsync(string path, CancellationToken ct = default)
    {
        var file = await StorageFile.GetFileFromPathAsync(path).AsTask(ct);
        using IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.Read).AsTask(ct);
        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(ct);

        int width = (int)decoder.PixelWidth, height = (int)decoder.PixelHeight;

        // The engine rejects images larger than MaxImageDimension; scale down tall/wide
        // captures (scrolling screenshots) and map word boxes back to source pixels.
        double scale = Math.Min(1.0, (double)OcrEngine.MaxImageDimension / Math.Max(width, height));
        var transform = new BitmapTransform();
        if (scale < 1.0)
        {
            transform.ScaledWidth = (uint)(width * scale);
            transform.ScaledHeight = (uint)(height * scale);
            transform.InterpolationMode = BitmapInterpolationMode.Fant;
        }

        using var bitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage).AsTask(ct);

        var result = await _engine.RecognizeAsync(bitmap).AsTask(ct);

        var lines = result.Lines.Select(line => new OcrLine(
            line.Text,
            line.Words.Select(w => new OcrWord(
                w.Text,
                w.BoundingRect.X / scale, w.BoundingRect.Y / scale,
                w.BoundingRect.Width / scale, w.BoundingRect.Height / scale)).ToList())).ToList();

        return new OcrPage(width, height, lines);
    }
}
