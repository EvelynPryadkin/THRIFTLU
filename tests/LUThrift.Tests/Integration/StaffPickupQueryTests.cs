using System.Data.Common;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using LUThrift.Web.Authorization;
using LUThrift.Web.Data;
using LUThrift.Web.InputModels;
using LUThrift.Web.Models;
using LUThrift.Web.Services;
using LUThrift.Web.ViewModels;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace LUThrift.Tests.Integration;

public sealed class StaffPickupQueryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 15, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(ApplicationRoles.Staff)]
    [InlineData(ApplicationRoles.Administrator)]
    public async Task CurrentStaffOrAdministratorMembership_AllowsAccessWithoutRoleClaims(string role)
    {
        await using var test = await TestContext.CreateAsync(role);
        await test.SeedAsync(test.Reservation(101));

        var page = Page(await test.Service.GetPageAsync(new(), test.Actor));

        Assert.Equal(101, Assert.Single(page.Items).ReservationId);
        Assert.Equal(1, page.TotalCount);
        Assert.Equal(1, test.Clock.ReadCount);
        Assert.Empty(test.Database.ChangeTracker.Entries());
    }

    [Theory]
    [InlineData("null", StaffPickupQueryStatus.AuthenticationRequired)]
    [InlineData("anonymous", StaffPickupQueryStatus.AuthenticationRequired)]
    [InlineData("missing identifier", StaffPickupQueryStatus.AuthenticationRequired)]
    [InlineData("blank identifier", StaffPickupQueryStatus.AuthenticationRequired)]
    [InlineData("long identifier", StaffPickupQueryStatus.AuthenticationRequired)]
    [InlineData("unknown account", StaffPickupQueryStatus.StaffRequired)]
    [InlineData("student with forged claims", StaffPickupQueryStatus.StaffRequired)]
    [InlineData("inactive account", StaffPickupQueryStatus.StaffRequired)]
    [InlineData("revoked membership", StaffPickupQueryStatus.StaffRequired)]
    public async Task DeniedIdentity_DoesNotReadReservationsOrExposeStudentData(
        string scenario, StaffPickupQueryStatus expected)
    {
        await using var test = await TestContext.CreateAsync();
        await test.SeedAsync(test.Reservation(101));
        ClaimsPrincipal? caller = Principal(TestContext.ActorId, privilegedClaim: true);
        switch (scenario)
        {
            case "null": caller = null; break;
            case "anonymous": caller = Principal(TestContext.ActorId, authenticated: false, privilegedClaim: true); break;
            case "missing identifier": caller = Principal(null, privilegedClaim: true); break;
            case "blank identifier": caller = Principal(" ", privilegedClaim: true); break;
            case "long identifier": caller = Principal(new string('a', 451), privilegedClaim: true); break;
            case "unknown account": caller = Principal(Guid.NewGuid().ToString(), privilegedClaim: true); break;
            case "student with forged claims": caller = Principal(TestContext.StudentId, privilegedClaim: true); break;
            case "inactive account":
                await test.Database.Users.Where(row => row.Id == TestContext.ActorId)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.IsActive, false));
                break;
            case "revoked membership":
                await test.Database.UserRoles.Where(row => row.UserId == TestContext.ActorId).ExecuteDeleteAsync();
                break;
        }
        test.Commands.Clear();

        var result = await test.Service.GetPageAsync(new() { ReservationId = 101 }, caller);

        Assert.Equal(expected, result.Status);
        Assert.False(result.Succeeded);
        Assert.Null(result.Page);
        Assert.DoesNotContain(test.StudentEmail, result.Message);
        Assert.DoesNotContain(test.Commands.Statements, IsReservationQuery);
        Assert.Equal(0, test.Clock.ReadCount);
        Assert.Empty(test.Database.ChangeTracker.Entries());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("zero id")]
    [InlineData("negative id")]
    [InlineData("long email")]
    [InlineData("nul")]
    [InlineData("control")]
    [InlineData("unicode control")]
    [InlineData("delete")]
    [InlineData("invalid unicode")]
    public async Task InvalidInput_IsRejectedBeforeReservationQueries(string scenario)
    {
        await using var test = await TestContext.CreateAsync();
        await test.SeedAsync(test.Reservation(101));
        StaffPickupQueryInput? input = scenario switch
        {
            "null" => null,
            "zero id" => new() { ReservationId = 0 },
            "negative id" => new() { ReservationId = -1 },
            "long email" => new() { StudentEmail = new string('x', 257) },
            "nul" => new() { StudentEmail = "part\0@example.test" },
            "control" => new() { StudentEmail = "part\u0001@example.test" },
            "unicode control" => new() { StudentEmail = "part\u0085@example.test" },
            "delete" => new() { StudentEmail = "part\u007f@example.test" },
            "invalid unicode" => new() { StudentEmail = "part\ud800@example.test" },
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
        test.Commands.Clear();

        var result = await test.Service.GetPageAsync(input, test.Actor);

        Assert.Equal(StaffPickupQueryStatus.InvalidInput, result.Status);
        Assert.Null(result.Page);
        Assert.DoesNotContain(test.Commands.Statements, IsReservationQuery);
        Assert.Equal(0, test.Clock.ReadCount);
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    public async Task DeadlineBoundary_ControlsDefaultVisibilityAndExactLookupWithoutWriting(long ticks, bool active)
    {
        await using var test = await TestContext.CreateAsync(now: Now.AddTicks(ticks));
        var reservation = test.Reservation(101, expires: Now);
        await test.SeedAsync(reservation);
        var before = await test.SnapshotAsync();

        var defaultPage = Page(await test.Service.GetPageAsync(new(), test.Actor));
        var exactPage = Page(await test.Service.GetPageAsync(new() { ReservationId = reservation.Id }, test.Actor));

        Assert.Equal(active ? 1 : 0, defaultPage.TotalCount);
        Assert.Equal(active ? 1 : 0, defaultPage.Items.Count);
        var row = Assert.Single(exactPage.Items);
        Assert.Equal(active ? ReservationStatus.Active : ReservationStatus.Expired, row.ReservationStatus);
        Assert.Equal(ListingStatus.Held, row.ListingStatus);
        Assert.Equal(reservation.ExpiresAtUtc, row.ExpiresAtUtc);
        Assert.Equal(2, test.Clock.ReadCount);
        await test.AssertUnchangedAsync(before);
    }

    [Theory]
    [InlineData(ReservationStatus.Canceled)]
    [InlineData(ReservationStatus.Expired)]
    [InlineData(ReservationStatus.Fulfilled)]
    public async Task ExactId_CanReadClosedReservationsWithoutTreatingFutureDeadlinesAsActive(ReservationStatus status)
    {
        await using var test = await TestContext.CreateAsync();
        await test.SeedAsync(test.Reservation(101, status: status));

        Assert.Empty(Page(await test.Service.GetPageAsync(new(), test.Actor)).Items);
        var row = Assert.Single(Page(await test.Service.GetPageAsync(new() { ReservationId = 101 }, test.Actor)).Items);

        Assert.Equal(status, row.ReservationStatus);
        Assert.Equal(Now.AddMinutes(30).UtcDateTime, row.ExpiresAtUtc);
    }

    [Fact]
    public async Task ExactIdAndEmail_AreCombinedAndUnknownIdsDoNotBroadenTheSearch()
    {
        await using var test = await TestContext.CreateAsync();
        await test.SeedAsync(test.Reservation(101, status: ReservationStatus.Canceled),
            test.Reservation(102, userId: TestContext.OtherStudentId));

        var match = Page(await test.Service.GetPageAsync(new()
        {
            ReservationId = 101, StudentEmail = test.StudentEmail.ToLowerInvariant()
        }, test.Actor));
        Assert.Equal(101, Assert.Single(match.Items).ReservationId);
        foreach (var id in new[] { 101, int.MaxValue })
        {
            var mismatch = Page(await test.Service.GetPageAsync(new()
            {
                ReservationId = id, StudentEmail = test.OtherStudentEmail
            }, test.Actor));
            Assert.Empty(mismatch.Items);
            Assert.Equal(0, mismatch.TotalCount);
        }
    }

    [Fact]
    public async Task EmailSearch_NormalizesTrimsAndMatchesLiteralSubstringsWhileHandlingMissingEmail()
    {
        await using var test = await TestContext.CreateAsync();
        var literalId = await test.AddStudentAsync($"Literal%_!\\.{Guid.NewGuid():N}@example.test");
        var noEmailId = await test.AddStudentAsync(null);
        await test.SeedAsync(test.Reservation(101), test.Reservation(102, userId: TestContext.OtherStudentId),
            test.Reservation(103, userId: literalId), test.Reservation(104, userId: noEmailId));

        var mixedCase = Page(await test.Service.GetPageAsync(new() { StudentEmail = "  mIxEd.  " }, test.Actor));
        Assert.Equal(101, Assert.Single(mixedCase.Items).ReservationId);
        Assert.Equal(test.StudentEmail, mixedCase.Items[0].StudentEmail);
        foreach (var literal in new[] { "%", "_", "!", "\\" })
        {
            var page = Page(await test.Service.GetPageAsync(new() { StudentEmail = literal }, test.Actor));
            Assert.Equal(103, Assert.Single(page.Items).ReservationId);
        }
        var whitespace = Page(await test.Service.GetPageAsync(new() { StudentEmail = " \t\r\n " }, test.Actor));
        Assert.Equal(4, whitespace.TotalCount);
        Assert.Null(Assert.Single(whitespace.Items, row => row.ReservationId == 104).StudentEmail);
        var maximumLength = Page(await test.Service.GetPageAsync(new() { StudentEmail = new string('x', 256) }, test.Actor));
        Assert.Empty(maximumLength.Items);
    }

    [Fact]
    public async Task Pagination_FiltersBeforeCountAndReturnsStableNonoverlappingDeadlineOrderedPagesInSql()
    {
        await using var test = await TestContext.CreateAsync();
        var matching = Enumerable.Range(0, 41).Select(index => test.Reservation(100 + index,
            expires: Now.AddMinutes(1 + index / 4))).Reverse().ToArray();
        var otherOwner = Enumerable.Range(0, 5).Select(index => test.Reservation(200 + index,
            userId: TestContext.OtherStudentId, expires: Now.AddSeconds(1))).ToArray();
        await test.SeedAsync([.. matching, .. otherOwner,
            test.Reservation(210, status: ReservationStatus.Canceled),
            test.Reservation(211, status: ReservationStatus.Fulfilled),
            test.Reservation(212, status: ReservationStatus.Expired),
            test.Reservation(213, expires: Now)]);
        var before = await test.SnapshotAsync();
        test.Commands.Clear();
        var input = new StaffPickupQueryInput { StudentEmail = test.StudentEmail };

        var first = Page(await test.Service.GetPageAsync(input, test.Actor));
        var second = Page(await test.Service.GetPageAsync(input, test.Actor, 2));
        var third = Page(await test.Service.GetPageAsync(input, test.Actor, 3));
        var repeated = Page(await test.Service.GetPageAsync(input, test.Actor, 2));

        AssertPage(first, 41, 1, 20);
        AssertPage(second, 41, 2, 20);
        AssertPage(third, 41, 3, 1);
        Assert.Equal(second.Items, repeated.Items);
        var combined = first.Items.Concat(second.Items).Concat(third.Items).ToArray();
        Assert.Equal(matching.OrderBy(row => row.ExpiresAtUtc).ThenBy(row => row.Id).Select(row => row.Id),
            combined.Select(row => row.ReservationId));
        Assert.Equal(41, combined.Select(row => row.ReservationId).Distinct().Count());
        Assert.Equal(4, test.Clock.ReadCount);
        var reservationSql = test.Commands.Statements.Where(IsReservationQuery).ToArray();
        Assert.Equal(8, reservationSql.Length);
        Assert.Contains(reservationSql, sql => Regex.IsMatch(sql, @"\bCOUNT\s*\(", RegexOptions.IgnoreCase));
        Assert.Contains(reservationSql, sql => sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase)
            && sql.Contains("OFFSET", StringComparison.OrdinalIgnoreCase));
        await test.AssertUnchangedAsync(before);
    }

    [Theory]
    [InlineData(int.MinValue, 1)]
    [InlineData(0, 1)]
    [InlineData(int.MaxValue, 2)]
    public async Task PageNumber_ClampsBeforeOffsetArithmetic(int requestedPage, int expectedPage)
    {
        await using var test = await TestContext.CreateAsync();
        await test.SeedAsync(Enumerable.Range(100, 21).Select(id => test.Reservation(id)).ToArray());

        var page = Page(await test.Service.GetPageAsync(new(), test.Actor, requestedPage));

        AssertPage(page, 21, expectedPage, expectedPage == 1 ? 20 : 1);
        Assert.Equal(expectedPage == 1 ? 100 : 120, page.Items[0].ReservationId);
    }

    [Fact]
    public async Task EmptyResults_ReturnOneEmptyPage()
    {
        await using var test = await TestContext.CreateAsync();

        var page = Page(await test.Service.GetPageAsync(new(), test.Actor, int.MaxValue));

        AssertPage(page, 0, 1, 0);
    }

    [Fact]
    public async Task Projection_ReturnsOnlySevenDisplayFieldsAndKeepsInconsistentListingStatusVisibleWithoutWrites()
    {
        await using var test = await TestContext.CreateAsync();
        var reservation = test.Reservation(101);
        reservation.Listing.Status = ListingStatus.Missing;
        await test.SeedAsync(reservation);
        var before = await test.SnapshotAsync();

        var row = Assert.Single(Page(await test.Service.GetPageAsync(new(), test.Actor)).Items);

        Assert.Equal(101, row.ReservationId);
        Assert.Equal(reservation.Listing.Title, row.ItemTitle);
        Assert.Equal("Pickup student", row.StudentDisplayName);
        Assert.Equal(test.StudentEmail, row.StudentEmail);
        Assert.Equal(ReservationStatus.Active, row.ReservationStatus);
        Assert.Equal(ListingStatus.Missing, row.ListingStatus);
        Assert.Equal(reservation.ExpiresAtUtc, row.ExpiresAtUtc);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(row));
        Assert.Equal(new[] { "ExpiresAtUtc", "ItemTitle", "ListingStatus", "ReservationId", "ReservationStatus",
            "StudentDisplayName", "StudentEmail" },
            json.RootElement.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal));
        Assert.Equal(1, test.Clock.ReadCount);
        await test.AssertUnchangedAsync(before);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    public async Task RevocationBeforeCountOrRows_DoesNotFetchStudentRowsOrReturnPriorCounts(
        int reservationCommandNumber, bool deactivate)
    {
        await using var test = await TestContext.CreateAsync();
        await test.SeedAsync(test.Reservation(101));
        test.Commands.Clear();
        var queryCount = 0;
        var revoked = false;
        test.Commands.BeforeReservationQuery = async cancellationToken =>
        {
            if (++queryCount != reservationCommandNumber) return;
            await test.RevokeAccessAsync(deactivate, cancellationToken);
            revoked = true;
        };

        var result = await test.Service.GetPageAsync(new(), test.Actor);

        Assert.True(revoked);
        Assert.Equal(StaffPickupQueryStatus.StaffRequired, result.Status);
        Assert.Null(result.Page);
        Assert.False(Assert.Single(test.Commands.PageQueryHadRows));
        Assert.DoesNotContain(test.StudentEmail, result.Message);
        Assert.Empty(test.Database.ChangeTracker.Entries());
    }

    [Fact]
    public async Task CanceledRequest_StopsBeforeAnyDatabaseQuery()
    {
        await using var test = await TestContext.CreateAsync();
        test.Commands.Clear();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => test.Service.GetPageAsync(
            new(), test.Actor, cancellationToken: cancellation.Token));

        Assert.Empty(test.Commands.Statements);
        Assert.Equal(0, test.Clock.ReadCount);
    }

    private static StaffPickupPage Page(StaffPickupQueryResult result)
    {
        Assert.Equal(StaffPickupQueryStatus.Success, result.Status);
        Assert.True(result.Succeeded);
        return Assert.IsType<StaffPickupPage>(result.Page);
    }

    private static void AssertPage(StaffPickupPage page, int total, int number, int items)
    {
        Assert.Equal(total, page.TotalCount);
        Assert.Equal(number, page.PageNumber);
        Assert.Equal(items, page.Items.Count);
        Assert.Equal(total == 0 ? 1 : (total - 1) / 20 + 1, page.TotalPages);
        Assert.Equal(number > 1, page.HasPreviousPage);
        Assert.Equal(number < page.TotalPages, page.HasNextPage);
    }

    private static ClaimsPrincipal Principal(string? id, bool authenticated = true, bool privilegedClaim = false)
    {
        var claims = new List<Claim>();
        if (id is not null) claims.Add(new(ClaimTypes.NameIdentifier, id));
        if (privilegedClaim) claims.Add(new(ClaimTypes.Role, ApplicationRoles.Administrator));
        return new(new ClaimsIdentity(claims, authenticated ? "Pickup query test" : null));
    }

    private static bool IsReservationQuery(string sql) => sql.Contains("\"Reservations\"", StringComparison.Ordinal);

    private sealed class CountingClock(DateTimeOffset now) : TimeProvider
    {
        public int ReadCount { get; private set; }
        public override DateTimeOffset GetUtcNow()
        {
            ReadCount++;
            return now;
        }
    }

    private sealed class QueryRecorder : DbCommandInterceptor
    {
        public List<string> Statements { get; } = [];
        public List<bool> PageQueryHadRows { get; } = [];
        public Func<CancellationToken, Task>? BeforeReservationQuery { get; set; }
        public void Clear()
        {
            Statements.Clear();
            PageQueryHadRows.Clear();
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Statements.Add(command.CommandText);
            if (IsReservationQuery(command.CommandText) && BeforeReservationQuery is not null)
                await BeforeReservationQuery(cancellationToken);
            return result;
        }

        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (IsReservationQuery(command.CommandText)
                && !Regex.IsMatch(command.CommandText, @"\bCOUNT\s*\(", RegexOptions.IgnoreCase))
                PageQueryHadRows.Add(result.HasRows);
            return ValueTask.FromResult(result);
        }
    }

    private sealed record Snapshot(object[] Listings, object[] Reservations, object[] Audits);

    private sealed class TestContext(SqliteConnection connection, ApplicationDbContext database,
        CountingClock clock, QueryRecorder commands, string studentEmail, string otherStudentEmail) : IAsyncDisposable
    {
        public const string ActorId = "pickup-staff";
        public const string StudentId = "pickup-student";
        public const string OtherStudentId = "pickup-other-student";
        public ApplicationDbContext Database { get; } = database;
        public CountingClock Clock { get; } = clock;
        public QueryRecorder Commands { get; } = commands;
        public string StudentEmail { get; } = studentEmail;
        public string OtherStudentEmail { get; } = otherStudentEmail;
        public ClaimsPrincipal Actor { get; } = Principal(ActorId);
        public StaffPickupService Service { get; } = new(database, new UpperInvariantLookupNormalizer(), clock);

        public static async Task<TestContext> CreateAsync(string role = ApplicationRoles.Staff, DateTimeOffset? now = null)
        {
            var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
            await connection.OpenAsync();
            var commands = new QueryRecorder();
            var database = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(connection).AddInterceptors(commands).Options);
            await database.Database.EnsureCreatedAsync();
            var normalizer = new UpperInvariantLookupNormalizer();
            var studentEmail = $"MiXeD.{Guid.NewGuid():N}@example.test";
            var otherEmail = $"Other.{Guid.NewGuid():N}@example.test";
            database.Users.AddRange(new ApplicationUser { Id = ActorId, DisplayName = "Pickup staff" },
                new ApplicationUser { Id = StudentId, DisplayName = "Pickup student", Email = studentEmail,
                    NormalizedEmail = normalizer.NormalizeEmail(studentEmail) },
                new ApplicationUser { Id = OtherStudentId, DisplayName = "Other pickup student", Email = otherEmail,
                    NormalizedEmail = normalizer.NormalizeEmail(otherEmail) });
            database.Roles.Add(new IdentityRole { Id = "pickup-role", Name = role, NormalizedName = role.ToUpperInvariant() });
            database.Roles.Add(new IdentityRole { Id = "pickup-student-role", Name = ApplicationRoles.Student,
                NormalizedName = ApplicationRoles.Student.ToUpperInvariant() });
            database.UserRoles.AddRange(new IdentityUserRole<string> { UserId = ActorId, RoleId = "pickup-role" },
                new IdentityUserRole<string> { UserId = StudentId, RoleId = "pickup-student-role" },
                new IdentityUserRole<string> { UserId = OtherStudentId, RoleId = "pickup-student-role" });
            await database.SaveChangesAsync();
            database.ChangeTracker.Clear();
            commands.Clear();
            return new(connection, database, new CountingClock(now ?? Now), commands, studentEmail, otherEmail);
        }

        public async Task<string> AddStudentAsync(string? email)
        {
            var id = Guid.NewGuid().ToString();
            Database.Users.Add(new ApplicationUser { Id = id, DisplayName = "Additional pickup student", Email = email,
                NormalizedEmail = new UpperInvariantLookupNormalizer().NormalizeEmail(email) });
            await Database.SaveChangesAsync();
            Database.ChangeTracker.Clear();
            return id;
        }

        public Reservation Reservation(int id, string userId = StudentId,
            ReservationStatus status = ReservationStatus.Active, DateTimeOffset? expires = null) => new()
        {
            Id = id, UserId = userId, Status = status, CreatedAtUtc = Now.AddHours(-1).UtcDateTime,
            ExpiresAtUtc = (expires ?? Now.AddMinutes(30)).UtcDateTime,
            CancellationReason = status == ReservationStatus.Canceled ? "Private history detail" : null,
            FulfilledAtUtc = status == ReservationStatus.Fulfilled ? Now.UtcDateTime : null,
            Listing = new Listing { Id = id + 1000, Title = $"Pickup item {id}", Description = "Private item description",
                Condition = "Good", CategoryId = 1, Status = ListingStatus.Held, CreatedAtUtc = Now.AddDays(-1).UtcDateTime }
        };

        public async Task SeedAsync(params Reservation[] reservations)
        {
            Database.Reservations.AddRange(reservations);
            Database.AuditEvents.Add(new AuditEvent { Listing = reservations[0].Listing, ActorUserId = StudentId,
                Action = InventoryAuditActions.Reserved, Details = "Private audit detail", OccurredAtUtc = Now.AddHours(-1).UtcDateTime });
            await Database.SaveChangesAsync();
            Database.ChangeTracker.Clear();
        }

        public async Task RevokeAccessAsync(bool deactivate, CancellationToken cancellationToken)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = deactivate
                ? "UPDATE \"AspNetUsers\" SET \"IsActive\" = 0 WHERE \"Id\" = $actor"
                : "DELETE FROM \"AspNetUserRoles\" WHERE \"UserId\" = $actor";
            command.Parameters.AddWithValue("$actor", ActorId);
            Assert.Equal(1, await command.ExecuteNonQueryAsync(cancellationToken));
        }

        public async Task<Snapshot> SnapshotAsync()
        {
            var listings = await Database.Listings.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync();
            var reservations = await Database.Reservations.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync();
            var audits = await Database.AuditEvents.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync();
            return new(listings.Select(row => (object)(row.Id, row.Title, row.Description, row.Condition,
                    row.Size, row.CategoryId, row.Status, row.CreatedAtUtc)).ToArray(),
                reservations.Select(row => (object)(row.Id, row.ListingId, row.UserId, row.Status, row.CreatedAtUtc,
                    row.ExpiresAtUtc, row.FulfilledAtUtc, row.CancellationReason)).ToArray(),
                audits.Select(row => (object)(row.Id, row.ListingId, row.ActorUserId, row.Action, row.OccurredAtUtc, row.Details)).ToArray());
        }

        public async Task AssertUnchangedAsync(Snapshot before)
        {
            var after = await SnapshotAsync();
            Assert.Equal(before.Listings, after.Listings);
            Assert.Equal(before.Reservations, after.Reservations);
            Assert.Equal(before.Audits, after.Audits);
            Assert.Empty(Database.ChangeTracker.Entries());
        }

        public async ValueTask DisposeAsync()
        {
            await Database.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
