using LUThrift.Web.Areas.Identity.Pages.Account;
using LUThrift.Web.Data;
using LUThrift.Web.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LUThrift.Tests.Integration;

internal sealed class IdentityTestContext : IAsyncDisposable
{
    private readonly SqliteConnection connection;
    private readonly ServiceProvider provider;
    private readonly AsyncServiceScope scope;

    private IdentityTestContext(SqliteConnection connection, ServiceProvider provider)
    {
        this.connection = connection;
        this.provider = provider;
        scope = provider.CreateAsyncScope();
        Database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        Roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        SignIns = new RecordingSignInManager(Users, scope.ServiceProvider);
    }

    public ApplicationDbContext Database { get; }
    public UserManager<ApplicationUser> Users { get; }
    public RoleManager<IdentityRole> Roles { get; }
    public RecordingSignInManager SignIns { get; }

    public static async Task<IdentityTestContext> CreateAsync(
        string? roleCreationFailure = null, string? roleAssignmentFailure = null)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthentication();
        services.AddHttpContextAccessor();
        services.AddDbContext<ApplicationDbContext>(options => options.UseSqlite(connection));
        services.AddIdentityCore<ApplicationUser>(options =>
            {
                options.User.RequireUniqueEmail = true;
                options.Password.RequiredLength = 8;
                options.Password.RequireNonAlphanumeric = false;
                options.Stores.SchemaVersion = IdentitySchemaVersions.Version1;
                options.Stores.MaxLengthForKeys = 0;
            })
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<ApplicationDbContext>()
            .AddSignInManager();

        if (roleCreationFailure is not null)
        {
            services.AddScoped<IRoleValidator<IdentityRole>>(
                _ => new RejectingRoleValidator(roleCreationFailure));
        }

        if (roleAssignmentFailure is not null)
        {
            services.AddScoped<UserManager<ApplicationUser>>(serviceProvider =>
                new RejectingRoleUserManager(serviceProvider, roleAssignmentFailure));
        }

        var context = new IdentityTestContext(connection, services.BuildServiceProvider());
        await context.Database.Database.EnsureCreatedAsync();
        return context;
    }

    public RoleInitializer CreateInitializer(
        string environment = "Development",
        string? email = null,
        ILogger<RoleInitializer>? logger = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DevelopmentAdmin:Email"] = email
            })
            .Build();

        return new RoleInitializer(Roles, Users,
            new TestHostEnvironment { EnvironmentName = environment },
            configuration, logger ?? NullLogger<RoleInitializer>.Instance);
    }

    public RegisterModel CreateRegistration(RegisterModel.InputModel input)
    {
        var actionContext = new ActionContext(
            new DefaultHttpContext(), new RouteData(), new PageActionDescriptor());

        return new RegisterModel(Users, SignIns, Database, NullLogger<RegisterModel>.Instance)
        {
            PageContext = new PageContext(actionContext),
            Url = new UrlHelper(actionContext),
            Input = input
        };
    }

    public async Task<ApplicationUser> CreateUserAsync(string? email = null)
    {
        email ??= NewEmail();
        var user = new ApplicationUser
        {
            DisplayName = "Test student",
            UserName = email,
            Email = email
        };
        var result = await Users.CreateAsync(user, NewPassword());
        Assert.True(result.Succeeded);
        return user;
    }

    public static string NewEmail() => $"{Guid.NewGuid():N}@example.test";
    public static string NewPassword() => Convert.ToBase64String(
        System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)) + "Aa1";

    public async ValueTask DisposeAsync()
    {
        await scope.DisposeAsync();
        await provider.DisposeAsync();
        await connection.DisposeAsync();
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = nameof(IdentityTestContext);
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class RejectingRoleValidator(string description) : IRoleValidator<IdentityRole>
    {
        public Task<IdentityResult> ValidateAsync(RoleManager<IdentityRole> manager, IdentityRole role)
            => Task.FromResult(IdentityResult.Failed(new IdentityError
            {
                Code = "TestRoleCreationFailure",
                Description = description
            }));
    }

    private sealed class RejectingRoleUserManager(IServiceProvider services, string description)
        : UserManager<ApplicationUser>(
            services.GetRequiredService<IUserStore<ApplicationUser>>(),
            services.GetRequiredService<IOptions<IdentityOptions>>(),
            services.GetRequiredService<IPasswordHasher<ApplicationUser>>(),
            services.GetServices<IUserValidator<ApplicationUser>>(),
            services.GetServices<IPasswordValidator<ApplicationUser>>(),
            services.GetRequiredService<ILookupNormalizer>(),
            services.GetRequiredService<IdentityErrorDescriber>(),
            services,
            services.GetRequiredService<ILogger<UserManager<ApplicationUser>>>())
    {
        public override Task<IdentityResult> AddToRoleAsync(ApplicationUser user, string role)
            => Task.FromResult(IdentityResult.Failed(new IdentityError
            {
                Code = "TestRoleAssignmentFailure",
                Description = description
            }));
    }
}

internal sealed class RecordingSignInManager(UserManager<ApplicationUser> users, IServiceProvider services)
    : SignInManager<ApplicationUser>(
        users,
        services.GetRequiredService<IHttpContextAccessor>(),
        services.GetRequiredService<IUserClaimsPrincipalFactory<ApplicationUser>>(),
        services.GetRequiredService<IOptions<IdentityOptions>>(),
        services.GetRequiredService<ILogger<SignInManager<ApplicationUser>>>(),
        services.GetRequiredService<IAuthenticationSchemeProvider>(),
        services.GetRequiredService<IUserConfirmation<ApplicationUser>>())
{
    public List<(ApplicationUser User, bool IsPersistent, string[] Roles, bool HasActiveTransaction)> Calls { get; } = [];

    public override async Task SignInAsync(ApplicationUser user, bool isPersistent, string? authenticationMethod = null)
    {
        var roles = await UserManager.GetRolesAsync(user);
        var hasActiveTransaction = services.GetRequiredService<ApplicationDbContext>().Database.CurrentTransaction is not null;
        Calls.Add((user, isPersistent, roles.ToArray(), hasActiveTransaction));
    }
}

internal sealed class RecordingLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
        => Entries.Add((logLevel, formatter(state, exception)));
}
