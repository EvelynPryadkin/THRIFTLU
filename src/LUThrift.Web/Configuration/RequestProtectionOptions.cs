namespace LUThrift.Web.Configuration;

public sealed class RequestProtectionOptions
{
    public const string SectionName = "RequestProtection";

    public RateLimitWindowOptions Login { get; set; } = new() { PermitLimit = 10, WindowSeconds = 60 };
    public RateLimitWindowOptions Registration { get; set; } = new() { PermitLimit = 5, WindowSeconds = 600 };
    public RateLimitWindowOptions ResendConfirmation { get; set; } = new() { PermitLimit = 3, WindowSeconds = 600 };
    public RateLimitWindowOptions Reservations { get; set; } = new() { PermitLimit = 20, WindowSeconds = 60 };
    public RateLimitWindowOptions Pickups { get; set; } = new() { PermitLimit = 30, WindowSeconds = 60 };
    public RateLimitWindowOptions ImageUploads { get; set; } = new() { PermitLimit = 10, WindowSeconds = 60 };

    public bool IsValid() => new[] { Login, Registration, ResendConfirmation, Reservations, Pickups, ImageUploads }
        .All(limit => limit is not null && limit.PermitLimit is >= 1 and <= 1000
            && limit.WindowSeconds is >= 1 and <= 3600);
}

public sealed class RateLimitWindowOptions
{
    public int PermitLimit { get; set; }
    public int WindowSeconds { get; set; }
}
