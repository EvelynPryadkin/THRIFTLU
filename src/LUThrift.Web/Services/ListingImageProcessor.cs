using SkiaSharp;

namespace LUThrift.Web.Services;

public enum ImageProcessingStatus { Success, InvalidImage, TooLarge, PixelLimitExceeded }

public sealed record ProcessedListingImage(byte[] Bytes, string ContentType, string Extension, int Width, int Height);
public sealed record ImageProcessingResult(ImageProcessingStatus Status, ProcessedListingImage? Image = null);

// Decode and re-encode uploaded bytes. No browser filename, extension, or MIME
// declaration is used to decide which content is accepted or stored.
public sealed class ListingImageProcessor
{
    public const int MaximumUploadBytes = 5 * 1024 * 1024;
    public const long MaximumDecodedPixels = 20_000_000;
    public const int MaximumDimension = 8192;

    public async Task<ImageProcessingResult> ProcessAsync(Stream upload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(upload);
        cancellationToken.ThrowIfCancellationRequested();
        if (!upload.CanRead) return new(ImageProcessingStatus.InvalidImage);

        // Length is neither available nor trustworthy for every uploaded stream.
        // Read at most the limit plus one byte, including for non-seekable input.
        using var input = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var count = Math.Min(buffer.Length, MaximumUploadBytes + 1 - (int)input.Length);
            var read = await upload.ReadAsync(buffer.AsMemory(0, count), cancellationToken);
            if (read == 0) break;
            await input.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            if (input.Length > MaximumUploadBytes) return new(ImageProcessingStatus.TooLarge);
        }
        if (input.Length == 0) return new(ImageProcessingStatus.InvalidImage);

        using var data = SKData.CreateCopy(input.GetBuffer().AsSpan(0, (int)input.Length));
        using var codec = SKCodec.Create(data);
        if (codec is null || codec.EncodedFormat is not (SKEncodedImageFormat.Jpeg or SKEncodedImageFormat.Png))
            return new(ImageProcessingStatus.InvalidImage);

        var width = codec.Info.Width;
        var height = codec.Info.Height;
        if (width <= 0 || height <= 0) return new(ImageProcessingStatus.InvalidImage);
        // Check the header before allocating either full-size bitmap. Multiplication
        // uses long so malicious dimensions cannot wrap around the pixel limit.
        if (width > MaximumDimension || height > MaximumDimension || (long)width * height > MaximumDecodedPixels)
            return new(ImageProcessingStatus.PixelLimitExceeded);
        if (codec.FrameCount > 1) return new(ImageProcessingStatus.InvalidImage);

        cancellationToken.ThrowIfCancellationRequested();
        using var srgb = SKColorSpace.CreateSrgb();
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul, srgb);
        using var decoded = new SKBitmap(info);
        if (decoded.GetPixels() == IntPtr.Zero || codec.GetPixels(info, decoded.GetPixels()) != SKCodecResult.Success)
            return new(ImageProcessingStatus.InvalidImage);

        cancellationToken.ThrowIfCancellationRequested();
        var swapsAxes = codec.EncodedOrigin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var outputWidth = swapsAxes ? height : width;
        var outputHeight = swapsAxes ? width : height;
        // This fresh bitmap carries pixels only. The decoder converts to sRGB;
        // input EXIF, location, XMP, ICC profiles and comments are not copied.
        using var normalized = new SKBitmap(new SKImageInfo(outputWidth, outputHeight, SKColorType.Rgba8888, SKAlphaType.Premul));
        if (normalized.GetPixels() == IntPtr.Zero) return new(ImageProcessingStatus.InvalidImage);
        using (var canvas = new SKCanvas(normalized))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.SetMatrix(OrientationMatrix(codec.EncodedOrigin, width, height));
            canvas.DrawBitmap(decoded, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest));
        }
        using var image = SKImage.FromBitmap(normalized);
        using var encoded = image.Encode(codec.EncodedFormat, 90);
        if (encoded is null) return new(ImageProcessingStatus.InvalidImage);
        cancellationToken.ThrowIfCancellationRequested();
        var jpeg = codec.EncodedFormat == SKEncodedImageFormat.Jpeg;
        return new(ImageProcessingStatus.Success,
            new(encoded.ToArray(), jpeg ? "image/jpeg" : "image/png", jpeg ? ".jpg" : ".png", outputWidth, outputHeight));
    }

    private static SKMatrix OrientationMatrix(SKEncodedOrigin origin, int width, int height)
    {
        var (scaleX, skewX, transX, skewY, scaleY, transY) = origin switch
        {
            SKEncodedOrigin.TopRight => (-1, 0, width, 0, 1, 0),
            SKEncodedOrigin.BottomRight => (-1, 0, width, 0, -1, height),
            SKEncodedOrigin.BottomLeft => (1, 0, 0, 0, -1, height),
            SKEncodedOrigin.LeftTop => (0, 1, 0, 1, 0, 0),
            SKEncodedOrigin.RightTop => (0, -1, height, 1, 0, 0),
            SKEncodedOrigin.RightBottom => (0, -1, height, -1, 0, width),
            SKEncodedOrigin.LeftBottom => (0, 1, 0, -1, 0, width),
            _ => (1, 0, 0, 0, 1, 0)
        };
        return new SKMatrix
        {
            ScaleX = scaleX, SkewX = skewX, TransX = transX,
            SkewY = skewY, ScaleY = scaleY, TransY = transY, Persp2 = 1
        };
    }
}
