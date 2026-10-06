using LUThrift.Web.Authorization;
using LUThrift.Web.Data;
using LUThrift.Web.Models;
using LUThrift.Web.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeFolder("/Staff", Policies.StaffOrAdministrator);
    options.Conventions.AuthorizeFolder("/Admin", Policies.AdministratorOnly);
    options.Conventions.AuthorizeFolder("/Account");
    options.Conventions.AllowAnonymousToFolder("/Thrift");
    options.Conventions.AllowAnonymousToPage("/Index");
});

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException(
        "Set ConnectionStrings:DefaultConnection with dotnet user-secrets before starting the app.");
}

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(connectionString));

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
    .AddEntityFrameworkStores<ApplicationDbContext>();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Identity/Account/Login";
    options.AccessDeniedPath = "/Identity/Account/AccessDenied";
});

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(Policies.StaffOrAdministrator, policy =>
        policy.RequireAuthenticatedUser()
            .RequireRole(ApplicationRoles.Staff, ApplicationRoles.Administrator));
    options.AddPolicy(Policies.AdministratorOnly, policy =>
        policy.RequireAuthenticatedUser()
            .RequireRole(ApplicationRoles.Administrator));
});

builder.Services.AddScoped<RoleInitializer>();
builder.Services.AddScoped<ListingService>();

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    await scope.ServiceProvider.GetRequiredService<RoleInitializer>().InitializeAsync();
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorPages()
   .WithStaticAssets();

app.Run();
