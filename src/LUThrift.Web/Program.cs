using LUThrift.Web.Authorization;
using LUThrift.Web.Configuration;
using LUThrift.Web.Data;
using LUThrift.Web.Jobs;
using LUThrift.Web.Models;
using LUThrift.Web.Operations;
using LUThrift.Web.Security;
using LUThrift.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.AddOperationalSafeguards();

builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeFolder("/Staff", Policies.StaffOrAdministrator);
    options.Conventions.AuthorizeFolder("/Admin", Policies.AdministratorOnly);
    options.Conventions.AuthorizeFolder("/Account", Policies.ActiveAccount);
    options.Conventions.AllowAnonymousToFolder("/Thrift");
    options.Conventions.AllowAnonymousToPage("/Index");
    RequestProtection.ConfigurePages(options);
});

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException(
        "Set ConnectionStrings:DefaultConnection with dotnet user-secrets before starting the app.");
}

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(connectionString).EnableSensitiveDataLogging(false).EnableDetailedErrors(false));

builder.Services.AddDefaultIdentity<ApplicationUser>(options =>
    {
        options.User.RequireUniqueEmail = true;
        options.SignIn.RequireConfirmedAccount = false;
        options.SignIn.RequireConfirmedEmail = false;

        options.Password.RequiredLength = 8;
        options.Password.RequireDigit = true;
        options.Password.RequireLowercase = true;
        options.Password.RequireUppercase = true;
        options.Password.RequireNonAlphanumeric = false;

        // Preserve the Identity schema and unbounded keys from the Week 2 migration.
        options.Stores.SchemaVersion = IdentitySchemaVersions.Version1;
        options.Stores.MaxLengthForKeys = 0;
    })
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddSignInManager<CurrentAccountSignInManager>();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Identity/Account/Login";
    options.AccessDeniedPath = "/Identity/Account/AccessDenied";
});

builder.Services.AddRequestProtection(builder.Configuration, builder.Environment);

builder.Services.AddAuthorization(options =>
{
    var activeAccount = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .AddRequirements(new CurrentAccountRequirement(CurrentAccountPermission.ActiveAccount))
        .Build();
    options.AddPolicy(Policies.ActiveAccount, activeAccount);
    // Also cover other [Authorize] pages, including Identity account management.
    // No fallback policy: public browsing and the anonymous logout page remain available.
    options.DefaultPolicy = activeAccount;
    options.AddPolicy(Policies.StaffOrAdministrator, policy =>
        policy.RequireAuthenticatedUser()
            .RequireRole(ApplicationRoles.Staff, ApplicationRoles.Administrator)
            .AddRequirements(new CurrentAccountRequirement(CurrentAccountPermission.StaffOrAdministrator)));
    options.AddPolicy(Policies.AdministratorOnly, policy =>
        policy.RequireAuthenticatedUser()
            .RequireRole(ApplicationRoles.Administrator)
            .AddRequirements(new CurrentAccountRequirement(CurrentAccountPermission.Administrator)));
});
builder.Services.AddScoped<IAuthorizationHandler, CurrentAccountHandler>();

builder.Services.AddScoped<RoleInitializer>();
builder.Services.AddScoped<ListingService>();
builder.Services.Configure<ImageStorageOptions>(builder.Configuration.GetSection(ImageStorageOptions.SectionName));
builder.Services.AddSingleton<ListingImageProcessor>();
builder.Services.AddScoped<ImageService>();
builder.Services.AddScoped<CatalogService>();
builder.Services.Configure<ReservationOptions>(builder.Configuration.GetSection(ReservationOptions.SectionName));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ReservationPolicy>();
builder.Services.AddSingleton<PickupHoursEligibility>();
builder.Services.AddSingleton<CampusTimeFormatter>();
builder.Services.AddScoped<ReservationService>();
builder.Services.AddScoped<StaffPickupService>();
builder.Services.AddScoped<StaffAccessService>();
builder.Services.AddScoped<PickupScheduleService>();
builder.Services.AddHostedService<ExpireReservationsWorker>();

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    await scope.ServiceProvider.GetRequiredService<RoleInitializer>().InitializeAsync();
}

app.UseSafeRequestLogging();
app.UseRequestProtectionHeaders();

app.UseExceptionHandler(new ExceptionHandlerOptions
{
    ExceptionHandlingPath = "/Error",
    SuppressDiagnosticsCallback = _ => true
});

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStatusCodePagesWithReExecute("/Status/{0}");

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapOperationalHealthChecks();
app.MapStaticAssets();
app.MapRazorPages()
   .WithStaticAssets();

app.Run();
