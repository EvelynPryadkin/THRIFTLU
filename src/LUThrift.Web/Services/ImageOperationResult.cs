namespace LUThrift.Web.Services;

public enum ImageOperationStatus
{
    Success,
    InvalidActor,
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
