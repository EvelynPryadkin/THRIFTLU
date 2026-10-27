using System.Collections.Concurrent;
using System.Data.Common;
using System.Threading.Channels;
using LUThrift.Web.Configuration;
using LUThrift.Web.Data;
using LUThrift.Web.Jobs;
using LUThrift.Web.Models;
using LUThrift.Web.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LUThrift.Tests.Unit;

public sealed class ExpireReservationsWorkerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);
    private const string FailureDetail = "worker-test persistence detail";

    [Fact]
    public async Task Worker_UsesFreshDisposedScopesForBoundedPassesWithoutPickupHours()
    {
        await using var test = await TestContext.CreateAsync(2, new() { ExpirationBatchSize = 1 });
        await test.Worker.StartAsync(CancellationToken.None);

        var firstDelay = await test.Clock.NextDelayAsync();
        Assert.Equal(TimeSpan.FromSeconds(30), firstDelay.DueTime);
        Assert.Equal(1, await test.ExpiredCountAsync());
        Assert.Equal(1, test.Scopes.Created);
        Assert.Equal(1, test.Scopes.Disposed);

        firstDelay.Fire();
        await test.Clock.NextDelayAsync();
        Assert.Equal(2, await test.ExpiredCountAsync());
        Assert.Equal(2, test.Scopes.Created);
        Assert.Equal(2, test.Scopes.Disposed);
        Assert.Equal(2, await test.Database.AuditEvents.CountAsync());
        Assert.Empty(test.Logger.Entries);
    }

    [Theory]
    [InlineData(0, 0, 1, 1)]
    [InlineData(int.MaxValue, int.MaxValue, 300, 200)]
    public async Task Worker_ClampsPollIntervalAndBatchSize(
        int pollSeconds, int batchSize, int expectedSeconds, int expectedBatchSize)
    {
        await using var test = await TestContext.CreateAsync(expectedBatchSize + 1, new()
        {
            ExpirationPollSeconds = pollSeconds,
            ExpirationBatchSize = batchSize
        });
        await test.Worker.StartAsync(CancellationToken.None);

        var delay = await test.Clock.NextDelayAsync();
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), delay.DueTime);
        Assert.Equal(expectedBatchSize, await test.ExpiredCountAsync());
        Assert.Equal(1, await test.Database.Reservations.CountAsync(row => row.Status == ReservationStatus.Active));
        Assert.Equal(1, test.Scopes.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Worker_RetriesFailuresInANewScopeWithoutLoggingSensitiveDetails(bool failDuringBatch)
    {
        await using var test = await TestContext.CreateAsync(1, new(), failFirstResolution: !failDuringBatch);
        if (failDuringBatch)
        {
            await test.Database.Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER fail_expiration_audit BEFORE INSERT ON AuditEvents
                BEGIN SELECT RAISE(ABORT, 'worker-test persistence detail'); END;
                """);
        }
        await test.Worker.StartAsync(CancellationToken.None);

        var firstDelay = await test.Clock.NextDelayAsync();
        Assert.Equal(0, await test.ExpiredCountAsync());
        Assert.Equal(1, test.Scopes.Created);
        Assert.Equal(1, test.Scopes.Disposed);
        Assert.Contains(test.Logger.Entries, entry => entry.Level ==
            (failDuringBatch ? LogLevel.Warning : LogLevel.Error));
        Assert.All(test.Logger.Entries, entry =>
        {
            Assert.DoesNotContain(FailureDetail, entry.Message);
            Assert.Null(entry.Exception);
        });

        if (failDuringBatch)
            await test.Database.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_expiration_audit;");
        firstDelay.Fire();
        await test.Clock.NextDelayAsync();

        Assert.Equal(1, await test.ExpiredCountAsync());
        Assert.Single(await test.Database.AuditEvents.ToListAsync());
        Assert.Equal(2, test.Scopes.Created);
        Assert.Equal(2, test.Scopes.Disposed);
    }

    [Fact]
    public async Task Worker_ShutdownCancelsPendingDelayWithoutAnotherPassOrError()
    {
        await using var test = await TestContext.CreateAsync(0, new());
        await test.Worker.StartAsync(CancellationToken.None);
        var delay = await test.Clock.NextDelayAsync();

        await test.Worker.StopAsync(CancellationToken.None).WaitAsync(TestTimeout);

        Assert.True(delay.IsDisposed);
        delay.Fire();
        Assert.Equal(1, test.Scopes.Created);
        Assert.Equal(1, test.Scopes.Disposed);
        Assert.Empty(test.Logger.Entries);
    }

    [Fact]
    public async Task Worker_ShutdownCancelsDatabaseWorkAndDisposesItsScope()
    {
        var interceptor = new BlockUntilCanceledInterceptor();
        await using var test = await TestContext.CreateAsync(1, new(), interceptor: interceptor);
        interceptor.Enabled = true;
        await test.Worker.StartAsync(CancellationToken.None);
        await interceptor.Started.Task.WaitAsync(TestTimeout);

        await test.Worker.StopAsync(CancellationToken.None).WaitAsync(TestTimeout);

        Assert.True(interceptor.CancellationObserved);
        Assert.Equal(1, test.Scopes.Created);
        Assert.Equal(1, test.Scopes.Disposed);
        Assert.Empty(test.Logger.Entries);
        interceptor.Enabled = false;
        Assert.Equal(0, await test.ExpiredCountAsync());
    }

    private sealed class TestContext(
        SqliteConnection connection,
        ServiceProvider services,
        ApplicationDbContext database,
        ExpireReservationsWorker worker,
        ControlledClock clock,
        ScopeCounts scopes,
        RecordingLogger logger) : IAsyncDisposable
    {
        public ApplicationDbContext Database { get; } = database;
        public ExpireReservationsWorker Worker { get; } = worker;
        public ControlledClock Clock { get; } = clock;
        public ScopeCounts Scopes { get; } = scopes;
        public RecordingLogger Logger { get; } = logger;

        public static async Task<TestContext> CreateAsync(int expiredReservations, ReservationOptions options,
            bool failFirstResolution = false, DbCommandInterceptor? interceptor = null)
        {
            var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
            await connection.OpenAsync();
            var builder = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection);
            if (interceptor is not null) builder.AddInterceptors(interceptor);
            var database = new ApplicationDbContext(builder.Options);
            await database.Database.EnsureCreatedAsync();
            var userId = Guid.NewGuid().ToString();
            database.Users.Add(new ApplicationUser { Id = userId, DisplayName = "Worker test account" });
            for (var index = 0; index < expiredReservations; index++)
            {
                database.Reservations.Add(new Reservation
                {
                    UserId = userId,
                    Listing = new Listing
                    {
                        Title = $"Expired hold {index}", Description = "Worker test listing",
                        Condition = "Good", CategoryId = 1, Status = ListingStatus.Held,
                        CreatedAtUtc = Now.AddHours(-2).UtcDateTime
                    },
                    CreatedAtUtc = Now.AddHours(-1).UtcDateTime,
                    ExpiresAtUtc = Now.UtcDateTime,
                    Status = ReservationStatus.Active
                });
            }
            await database.SaveChangesAsync();
            database.ChangeTracker.Clear();

            var clock = new ControlledClock();
            var scopes = new ScopeCounts();
            var logger = new RecordingLogger();
            var resolutionCount = 0;
            var registrations = new ServiceCollection();
            registrations.AddLogging();
            registrations.AddSingleton<TimeProvider>(clock);
            registrations.AddSingleton<IOptions<ReservationOptions>>(Options.Create(options));
            registrations.AddSingleton<PickupHoursEligibility>();
            registrations.AddScoped(_ => new ScopeProbe(scopes));
            registrations.AddScoped(_ => new ApplicationDbContext(builder.Options));
            registrations.AddScoped(provider =>
            {
                provider.GetRequiredService<ScopeProbe>();
                if (Interlocked.Increment(ref resolutionCount) == 1 && failFirstResolution)
                    throw new InvalidOperationException(FailureDetail);
                return ActivatorUtilities.CreateInstance<ReservationService>(provider);
            });
            var services = registrations.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
            var worker = new ExpireReservationsWorker(services.GetRequiredService<IServiceScopeFactory>(),
                Options.Create(options), clock, logger);
            return new(connection, services, database, worker, clock, scopes, logger);
        }

        public Task<int> ExpiredCountAsync()
            => Database.Reservations.CountAsync(row => row.Status == ReservationStatus.Expired);

        public async ValueTask DisposeAsync()
        {
            await Worker.StopAsync(CancellationToken.None).WaitAsync(TestTimeout);
            Worker.Dispose();
            await services.DisposeAsync();
            await Database.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class ScopeCounts
    {
        public int Created;
        public int Disposed;
    }

    private sealed class ScopeProbe : IAsyncDisposable
    {
        private readonly ScopeCounts counts;

        public ScopeProbe(ScopeCounts counts)
        {
            this.counts = counts;
            Interlocked.Increment(ref counts.Created);
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref counts.Disposed);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ControlledClock : TimeProvider
    {
        private readonly Channel<ControlledTimer> timers = Channel.CreateUnbounded<ControlledTimer>();

        public override DateTimeOffset GetUtcNow() => Now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ControlledTimer(callback, state, dueTime);
            timers.Writer.TryWrite(timer);
            return timer;
        }

        public Task<ControlledTimer> NextDelayAsync()
            => timers.Reader.ReadAsync().AsTask().WaitAsync(TestTimeout);
    }

    private sealed class ControlledTimer(TimerCallback callback, object? state, TimeSpan dueTime) : ITimer
    {
        private int disposed;
        public TimeSpan DueTime { get; } = dueTime;
        public bool IsDisposed => Volatile.Read(ref disposed) != 0;
        public void Fire()
        {
            if (!IsDisposed) callback(state);
        }
        public bool Change(TimeSpan dueTime, TimeSpan period) => !IsDisposed;
        public void Dispose() => Interlocked.Exchange(ref disposed, 1);
        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingLogger : ILogger<ExpireReservationsWorker>
    {
        public ConcurrentQueue<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Enqueue((logLevel, formatter(state, exception), exception));
    }

    private sealed class BlockUntilCanceledInterceptor : DbCommandInterceptor
    {
        public bool Enabled { get; set; }
        public bool CancellationObserved { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (!Enabled) return result;
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                CancellationObserved = true;
                throw;
            }
            return result;
        }
    }
}
