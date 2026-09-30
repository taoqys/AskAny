using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AskAny.Models;

namespace AskAny.Services;

public static class ImageAttachmentService
{
    public const int MaximumImageCount = 4;
    public const int MaximumDimension = 2048;

    private const long MaximumEncodedBytes = 5 * 1024 * 1024;

    public static async Task<ImageAttachment> FromFileAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        var source = CreateBitmapSource(bytes);
        var extension = Path.GetExtension(path);
        var preferPng = extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
                        extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase) ||
                        extension.Equals(".gif", StringComparison.OrdinalIgnoreCase);
        return Normalize(source, Path.GetFileName(path), preferPng);
    }

    public static ImageAttachment FromBitmap(
        Bitmap bitmap,
        string fileName,
        bool preferPng = true)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        stream.Position = 0;
        return Normalize(CreateBitmapSource(stream.ToArray()), fileName, preferPng);
    }

    public static ImageAttachment FromBitmapSource(
        BitmapSource source,
        string fileName,
        bool preferPng = false)
    {
        return Normalize(source, fileName, preferPng);
    }

    private static ImageAttachment Normalize(
        BitmapSource source,
        string fileName,
        bool preferPng)
    {
        var normalizedSource = ResizeIfNeeded(source, MaximumDimension);
        var encoded = Encode(normalizedSource, preferPng, 88);

        if (encoded.Bytes.LongLength > MaximumEncodedBytes)
        {
            normalizedSource = ResizeIfNeeded(normalizedSource, 1600);
            encoded = Encode(normalizedSource, false, 80);
        }

        if (encoded.Bytes.LongLength > MaximumEncodedBytes)
        {
            normalizedSource = ResizeIfNeeded(normalizedSource, 1200);
            encoded = Encode(normalizedSource, false, 72);
        }

        return new ImageAttachment
        {
            FileName = string.IsNullOrWhiteSpace(fileName) ? "image" : fileName,
            MediaType = encoded.MediaType,
            Bytes = encoded.Bytes,
            PixelWidth = normalizedSource.PixelWidth,
            PixelHeight = normalizedSource.PixelHeight,
            Preview = CreatePreview(encoded.Bytes)
        };
    }

    private static BitmapSource ResizeIfNeeded(BitmapSource source, int maximumDimension)
    {
        var longestSide = Math.Max(source.PixelWidth, source.PixelHeight);
        if (longestSide <= maximumDimension)
        {
            return source;
        }

        var scale = maximumDimension / (double)longestSide;
        var transformed = new TransformedBitmap(
            source,
            new ScaleTransform(scale, scale));
        transformed.Freeze();
        return transformed;
    }

    private static EncodedImage Encode(
        BitmapSource source,
        bool preferPng,
        int jpegQuality)
    {
        BitmapEncoder encoder = preferPng
            ? new PngBitmapEncoder()
            : CreateJpegEncoder(jpegQuality);
        encoder.Frames.Add(BitmapFrame.Create(source));

        using var stream = new MemoryStream();
        encoder.Save(stream);
        return new EncodedImage(
            stream.ToArray(),
            preferPng ? "image/png" : "image/jpeg");
    }

    private static JpegBitmapEncoder CreateJpegEncoder(int quality)
    {
        var encoder = new JpegBitmapEncoder
        {
            QualityLevel = quality
        };
        return encoder;
    }

    private static BitmapSource CreateBitmapSource(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static BitmapSource CreatePreview(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = 120;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private sealed record EncodedImage(byte[] Bytes, string MediaType);
}
