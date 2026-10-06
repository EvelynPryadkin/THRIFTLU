using System.ComponentModel.DataAnnotations;

namespace LUThrift.Web.Services;

public enum ListingResultStatus
{
    Success,
    InvalidInput,
    InvalidActor,
    CategoryNotFound,
    NotFound,
    NotEditable
}

public sealed class ListingResult<T>
{
    private ListingResult(ListingResultStatus status, T? value, IReadOnlyList<ValidationResult> errors)
    {
        Status = status;
        Value = value;
        Errors = errors;
    }

    public ListingResultStatus Status { get; }
    public bool Succeeded => Status == ListingResultStatus.Success;
    // Check Succeeded before using Value; failures carry errors instead.
    public T? Value { get; }
    public IReadOnlyList<ValidationResult> Errors { get; }

    internal static ListingResult<T> Success(T value) => new(ListingResultStatus.Success, value, []);

    internal static ListingResult<T> Failure(ListingResultStatus status, params ValidationResult[] errors)
        => new(status, default, errors);
}
