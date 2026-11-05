using System.Buffers.Binary;
using System.Text;
using LUThrift.Web.Services;
using SkiaSharp;

namespace LUThrift.Tests.Integration;

public sealed class ListingImageProcessorTests
{
    private readonly ListingImageProcessor processor = new();

    [Theory]
    [InlineData(SKEncodedImageFormat.Jpeg, "image/jpeg", ".jpg")]
    [InlineData(SKEncodedImageFormat.Png, "image/png", ".png")]
    public async Task ValidContent_IsDecodedAndReencodedWithServerDeterminedType(
        SKEncodedImageFormat format, string contentType, string extension)
    {
        var bytes = Image(format);
        using var upload = new MemoryStream(bytes);

        var result = await processor.ProcessAsync(upload);

        Assert.Equal(ImageProcessingStatus.Success, result.Status);
        var saved = Assert.IsType<ProcessedListingImage>(result.Image);
        Assert.Equal(contentType, saved.ContentType);
        Assert.Equal(extension, saved.Extension);
        Assert.Equal(80, saved.Width);
        Assert.Equal(40, saved.Height);
        using var decoded = SKBitmap.Decode(saved.Bytes);
        Assert.NotNull(decoded);
        Assert.Equal(saved.Width, decoded.Width);
        Assert.Equal(saved.Height, decoded.Height);
        AssertColor(SKColors.Red, decoded.GetPixel(10, 10));
    }

    [Fact]
    public async Task TransparentPng_PreservesTransparency()
    {
        using var bitmap = new SKBitmap(2, 2);
        bitmap.Erase(SKColors.Transparent);
        bitmap.SetPixel(1, 1, SKColors.Red);
        using var image = SKImage.FromBitmap(bitmap);
        using var bytes = image.Encode(SKEncodedImageFormat.Png, 100);
        using var upload = new MemoryStream(bytes.ToArray());

        var result = await processor.ProcessAsync(upload);

        Assert.Equal(ImageProcessingStatus.Success, result.Status);
        using var decoded = SKBitmap.Decode(result.Image!.Bytes);
        Assert.Equal((byte)0, decoded.GetPixel(0, 0).Alpha);
        Assert.Equal(SKColors.Red, decoded.GetPixel(1, 1));
    }

    public static IEnumerable<object[]> InvalidFiles()
    {
        yield return [Array.Empty<byte>()];
        yield return [Encoding.UTF8.GetBytes("<html><script>alert('not a photo')</script></html>")];
        yield return [new byte[] { 0xff, 0xd8, 0xff, 0xe0, 0, 16, 0, 0, 0, 0 }];
        yield return [new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 0 }];
        // A valid GIF remains disallowed even if a browser labels it JPEG/PNG.
        yield return [Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7")];
    }

    [Theory]
    [MemberData(nameof(InvalidFiles))]
    public async Task CorruptOrSpoofedContent_IsRejected(byte[] bytes)
    {
        using var upload = new MemoryStream(bytes);
        var result = await processor.ProcessAsync(upload);
        Assert.Equal(ImageProcessingStatus.InvalidImage, result.Status);
        Assert.Null(result.Image);
    }

    [Theory]
    [InlineData(SKEncodedImageFormat.Jpeg)]
    [InlineData(SKEncodedImageFormat.Png)]
    public async Task TruncatedPixelData_IsRejected(SKEncodedImageFormat format)
    {
        var bytes = Image(format);
        using var upload = new MemoryStream(bytes[..(bytes.Length * 3 / 4)]);
        var result = await processor.ProcessAsync(upload);
        Assert.Equal(ImageProcessingStatus.InvalidImage, result.Status);
        Assert.Null(result.Image);
    }

    [Theory]
    [InlineData(-1, ImageProcessingStatus.Success)]
    [InlineData(0, ImageProcessingStatus.Success)]
    [InlineData(1, ImageProcessingStatus.TooLarge)]
    public async Task UploadLimit_UsesActualBytesIncludingNonSeekableStreams(int bytesOverLimit, ImageProcessingStatus expected)
    {
        var png = Image(SKEncodedImageFormat.Png);
        var comment = Enumerable.Repeat((byte)'X', ListingImageProcessor.MaximumUploadBytes + bytesOverLimit - png.Length - 12).ToArray();
        Encoding.ASCII.GetBytes("Comment\0").CopyTo(comment, 0);
        var padded = AddPngChunk(png, "tEXt", comment);
        using var upload = new NonSeekableStream(padded);

        var result = await processor.ProcessAsync(upload);

        Assert.Equal(expected, result.Status);
        Assert.InRange(upload.BytesRead, 1, ListingImageProcessor.MaximumUploadBytes + 1);
        if (expected == ImageProcessingStatus.Success)
        {
            Assert.NotNull(result.Image);
            Assert.True(result.Image.Bytes.Length < 10000);
        }
        else Assert.Null(result.Image);
    }

    [Theory]
    [InlineData(8193, 1)]
    [InlineData(1, 8193)]
    [InlineData(5000, 4001)]
    [InlineData(int.MaxValue, int.MaxValue)]
    public async Task ExcessiveDimensions_AreRejectedBeforePixelAllocation(int width, int height)
    {
        var png = Image(SKEncodedImageFormat.Png);
        BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(16, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(20, 4), height);
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(29, 4), Crc(png.AsSpan(12, 17)));
        using var upload = new MemoryStream(png);

        var result = await processor.ProcessAsync(upload);

        // The native codec may reject impossible dimensions while reading the header.
        if (width == int.MaxValue)
            Assert.Contains(result.Status, new[] { ImageProcessingStatus.PixelLimitExceeded, ImageProcessingStatus.InvalidImage });
        else
            Assert.Equal(ImageProcessingStatus.PixelLimitExceeded, result.Status);
        Assert.Null(result.Image);
    }

    [Theory]
    [InlineData(1, 0, 1, 2, 3)]
    [InlineData(2, 1, 0, 3, 2)]
    [InlineData(3, 3, 2, 1, 0)]
    [InlineData(4, 2, 3, 0, 1)]
    [InlineData(5, 0, 2, 1, 3)]
    [InlineData(6, 2, 0, 3, 1)]
    [InlineData(7, 3, 1, 2, 0)]
    [InlineData(8, 1, 3, 0, 2)]
    public async Task ExifOrientation_IsAppliedToPixelsAndNotCopiedToOutput(
        byte orientation, int topLeft, int topRight, int bottomLeft, int bottomRight)
    {
        var jpeg = WithExifOrientation(Image(SKEncodedImageFormat.Jpeg), orientation);
        using var upload = new MemoryStream(jpeg);
        var result = await processor.ProcessAsync(upload);

        Assert.Equal(ImageProcessingStatus.Success, result.Status);
        var saved = result.Image!;
        Assert.Equal(orientation >= 5 ? 40 : 80, saved.Width);
        Assert.Equal(orientation >= 5 ? 80 : 40, saved.Height);
        using var decoded = SKBitmap.Decode(saved.Bytes);
        SKColor[] colors = [SKColors.Red, SKColors.Lime, SKColors.Blue, SKColors.Yellow];
        AssertColor(colors[topLeft], decoded.GetPixel(decoded.Width / 4, decoded.Height / 4));
        AssertColor(colors[topRight], decoded.GetPixel(decoded.Width * 3 / 4, decoded.Height / 4));
        AssertColor(colors[bottomLeft], decoded.GetPixel(decoded.Width / 4, decoded.Height * 3 / 4));
        AssertColor(colors[bottomRight], decoded.GetPixel(decoded.Width * 3 / 4, decoded.Height * 3 / 4));
        Assert.DoesNotContain("Exif", Encoding.Latin1.GetString(saved.Bytes));
        using var codec = SKCodec.Create(new MemoryStream(saved.Bytes));
        Assert.Equal(SKEncodedOrigin.TopLeft, codec.EncodedOrigin);
    }

    [Theory]
    [InlineData(SKEncodedImageFormat.Jpeg)]
    [InlineData(SKEncodedImageFormat.Png)]
    public async Task PrivateMetadata_IsNotRetained(SKEncodedImageFormat format)
    {
        const string privateText = "synthetic-private-location-and-author";
        var bytes = Image(format);
        bytes = format == SKEncodedImageFormat.Jpeg
            ? AddJpegSegment(WithExifOrientation(bytes, 1), 0xfe, Encoding.ASCII.GetBytes(privateText))
            : AddPngChunk(bytes, "tEXt", Encoding.ASCII.GetBytes("Comment\0" + privateText));
        Assert.Contains(privateText, Encoding.Latin1.GetString(bytes));
        using var upload = new MemoryStream(bytes);

        var result = await processor.ProcessAsync(upload);

        Assert.Equal(ImageProcessingStatus.Success, result.Status);
        var encoded = Encoding.Latin1.GetString(result.Image!.Bytes);
        Assert.DoesNotContain(privateText, encoded);
        Assert.DoesNotContain("Exif", encoded);
    }

    [Fact]
    public async Task CanceledProcessing_DoesNotReadInput()
    {
        using var upload = new NonSeekableStream(Image(SKEncodedImageFormat.Png));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processor.ProcessAsync(upload, cancellation.Token));
        Assert.Equal(0, upload.BytesRead);
    }

    private static byte[] Image(SKEncodedImageFormat format)
    {
        using var bitmap = new SKBitmap(80, 40);
        using (var canvas = new SKCanvas(bitmap))
        using (var paint = new SKPaint())
        {
            SKColor[] colors = [SKColors.Red, SKColors.Lime, SKColors.Blue, SKColors.Yellow];
            for (var row = 0; row < 2; row++)
            for (var column = 0; column < 2; column++)
            {
                paint.Color = colors[row * 2 + column];
                canvas.DrawRect(column * 40, row * 20, 40, 20, paint);
            }
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 100);
        return data.ToArray();
    }

    private static void AssertColor(SKColor expected, SKColor actual)
    {
        Assert.InRange(Math.Abs(expected.Red - actual.Red), 0, 20);
        Assert.InRange(Math.Abs(expected.Green - actual.Green), 0, 20);
        Assert.InRange(Math.Abs(expected.Blue - actual.Blue), 0, 20);
    }

    private static byte[] WithExifOrientation(byte[] jpeg, byte orientation)
    {
        byte[] exif = [69, 120, 105, 102, 0, 0, 73, 73, 42, 0, 8, 0, 0, 0,
            1, 0, 18, 1, 3, 0, 1, 0, 0, 0, orientation, 0, 0, 0, 0, 0, 0, 0];
        return AddJpegSegment(jpeg, 0xe1, exif);
    }

    private static byte[] AddJpegSegment(byte[] jpeg, byte marker, byte[] data)
    {
        var segment = new byte[data.Length + 4];
        segment[0] = 0xff;
        segment[1] = marker;
        BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(2, 2), (ushort)(data.Length + 2));
        data.CopyTo(segment, 4);
        return [..jpeg[..2], ..segment, ..jpeg[2..]];
    }

    private static byte[] AddPngChunk(byte[] png, string type, byte[] data)
    {
        var chunk = new byte[data.Length + 12];
        BinaryPrimitives.WriteInt32BigEndian(chunk.AsSpan(0, 4), data.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(chunk, 4);
        data.CopyTo(chunk, 8);
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(chunk.Length - 4), Crc(chunk.AsSpan(4, chunk.Length - 8)));
        return [..png[..33], ..chunk, ..png[33..]];
    }

    private static uint Crc(ReadOnlySpan<byte> bytes)
    {
        var crc = uint.MaxValue;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
        }
        return ~crc;
    }

    private sealed class NonSeekableStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream source = new(bytes);
        public int BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = source.Read(buffer, offset, count);
            BytesRead += read;
            return read;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            BytesRead += read;
            return read;
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) source.Dispose();
            base.Dispose(disposing);
        }
    }
}
