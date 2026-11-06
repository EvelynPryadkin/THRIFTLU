namespace LUThrift.Web.Services;

public enum ImageOperationStatus
{
    Success,
    AuthenticationRequired,
    StaffRequired,
    InvalidImage,
    TooLarge,
    PixelLimitExceeded,
    NotFound,
    NotEditable,
    Conflict,
    StorageUnavailable,
    PersistenceFailed
}

public sealed record ImageOperationResult(
    ImageOperationStatus Status, string Message, Guid? ImageId = null, bool CleanupPending = false)
{
    public bool Succeeded => Status == ImageOperationStatus.Success;
}

// The caller owns the stream. No storage filename or filesystem path leaves the service.
public sealed record ListingImageFile(Stream Content, string ContentType);
