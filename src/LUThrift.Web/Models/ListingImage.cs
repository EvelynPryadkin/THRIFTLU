namespace LUThrift.Web.Models;

// Each replacement gets a new ID. It is also the optimistic concurrency token
// supplied by a future staff form; it never grants permission to change a photo.
public sealed class ListingImage
{
    public Guid Id { get; set; }
    public int ListingId { get; set; }
    public Listing Listing { get; set; } = null!;
    public string StorageFileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long ByteLength { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
