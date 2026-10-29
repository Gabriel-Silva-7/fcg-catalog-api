using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FCG.Catalog.Application.Identity;
using FCG.Catalog.Infrastructure.Identity;
using FCG.Catalog.Infrastructure.Persistence;
using FCG.Contracts;
using MassTransit;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace FCG.Catalog.Api.Tests.Catalog;

public sealed class DistributedAuthorizationTests(CatalogPostgres postgres) : IClassFixture<CatalogPostgres>
{
    [Fact]
    public async Task Valid_jwt_without_projection_is_temporarily_unavailable_before_any_action()
    {
        await using var app = await CatalogTestApplication.StartAsync(postgres);
        app.Authorize(app.Token(role: "User", subject: Guid.NewGuid().ToString(), seedProjection: false));
        using var response = await app.Client.GetAsync("/api/v1/me/orders");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("1", response.Headers.RetryAfter?.ToString());
        Assert.Equal("auth_state_unavailable", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Same_signed_token_tracks_active_blocked_unblocked_deleted_state_without_reissue()
    {
        await using var app = await CatalogTestApplication.StartAsync(postgres);
        var id = Guid.NewGuid(); app.Authorize(app.Token("User", id.ToString(), seedProjection: false));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await app.Client.GetAsync("/api/v1/me/orders")).StatusCode);
        foreach (var (version, state, expected) in new[] {
            (1L, IdentityState.Active, HttpStatusCode.OK), (2L, IdentityState.Blocked, HttpStatusCode.Unauthorized),
            (3L, IdentityState.Active, HttpStatusCode.OK), (4L, IdentityState.Deleted, HttpStatusCode.Unauthorized) })
        {
            await Apply(app, Snapshot(id, version, state));
            Assert.Equal(expected, (await app.Client.GetAsync("/api/v1/me/orders")).StatusCode);
        }
        Assert.Equal(IdentityProjectionOutcome.Conflict, await Apply(app, Snapshot(id, 5, IdentityState.Active)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.Client.GetAsync("/api/v1/me/orders")).StatusCode);
    }

    [Theory]
    [InlineData("Administrator", IdentityRole.User, HttpStatusCode.Forbidden)]
    [InlineData("User", IdentityRole.Administrator, HttpStatusCode.Forbidden)]
    [InlineData("Administrator", IdentityRole.Administrator, HttpStatusCode.Created)]
    public async Task Administrative_write_requires_both_token_and_current_role(string tokenRole, IdentityRole currentRole, HttpStatusCode expected)
    {
        await using var app = await CatalogTestApplication.StartAsync(postgres);
        var id = Guid.NewGuid(); app.Authorize(app.Token(tokenRole, id.ToString(), seedProjection: false));
        await Apply(app, Snapshot(id, 2, IdentityState.Active, currentRole));
        var response = await app.Client.PostAsJsonAsync("/api/v1/games", new { title = "Role check", description = "Authorization", basePrice = 49.90m });
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(expected == HttpStatusCode.Created ? 1L : 0L, await app.ScalarAsync<long>("SELECT count(*) FROM \"Games\""));
    }

    [Theory]
    [InlineData("/api/v1/games", HttpStatusCode.OK)]
    [InlineData("/health/live", HttpStatusCode.OK)]
    [InlineData("/swagger/v1/swagger.json", HttpStatusCode.OK)]
    [InlineData("/does-not-exist", HttpStatusCode.NotFound)]
    public async Task Public_routes_do_not_consult_identity_even_with_valid_bearer(string path, HttpStatusCode expected)
    {
        var reader = new UnavailableReader();
        await using var app = await CatalogTestApplication.StartAsync(postgres, builder => builder.ConfigureTestServices(services =>
            services.Replace(ServiceDescriptor.Singleton<IAuthorizationStateReader>(reader))));
        app.Authorize(app.Token(seedProjection: false));
        Assert.Equal(expected, (await app.Client.GetAsync(path)).StatusCode);
        Assert.Equal(0, reader.Calls);
        app.Authorize("invalid");
        Assert.Equal(expected, (await app.Client.GetAsync(path)).StatusCode);
        Assert.Equal(0, reader.Calls);
    }

    [Fact]
    public async Task Storage_unavailable_fails_closed_but_invalid_tokens_are_rejected_before_read()
    {
        var reader = new UnavailableReader();
        await using var app = await CatalogTestApplication.StartAsync(postgres, builder => builder.ConfigureTestServices(services =>
            services.Replace(ServiceDescriptor.Singleton<IAuthorizationStateReader>(reader))));
        app.Authorize(app.Token(seedProjection: false));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await app.Client.GetAsync("/api/v1/me/orders")).StatusCode);
        Assert.Equal(1, reader.Calls);
        foreach (var token in new[] { app.Token(expirySeconds: -1, seedProjection: false), app.Token(wrongKey: true, seedProjection: false),
            app.Token(omitNotBefore: true, seedProjection: false), app.Token(duplicateSubject: true, seedProjection: false),
            app.Token(notBeforeSeconds: 20, seedProjection: false), "invalid" })
        {
            app.Authorize(token);
            Assert.Equal(HttpStatusCode.Unauthorized, (await app.Client.GetAsync("/api/v1/me/orders")).StatusCode);
        }
        Assert.Equal(1, reader.Calls);
        Assert.DoesNotContain("   at ", string.Join("\n", app.Logs.Entries));
        Assert.Contains(app.Logs.Entries, entry => entry.Contains("IdentityTokenRejected", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Token_expiry_uses_injected_clock_with_zero_skew()
    {
        var clock = new AdjustableClock();
        await using var app = await CatalogTestApplication.StartAsync(postgres, builder => builder.ConfigureTestServices(services =>
            services.Replace(ServiceDescriptor.Singleton<TimeProvider>(clock))));
        app.Authorize(app.Token(expirySeconds: 60));
        Assert.Equal(HttpStatusCode.OK, (await app.Client.GetAsync("/api/v1/me/orders")).StatusCode);
        clock.Now = DateTimeOffset.UtcNow.AddSeconds(61);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.Client.GetAsync("/api/v1/me/orders")).StatusCode);
    }

    [Fact]
    public async Task Reordered_and_repeated_snapshots_cannot_restore_older_authorization_or_retain_contact()
    {
        await using var app = await CatalogTestApplication.StartAsync(postgres); var id = Guid.NewGuid();
        var newest = Snapshot(id, 3, IdentityState.Blocked);
        Assert.Equal(IdentityProjectionOutcome.Applied, await Apply(app, newest));
        Assert.Equal(IdentityProjectionOutcome.Ignored, await Apply(app, Snapshot(id, 1, IdentityState.Active)));
        Assert.Equal(IdentityProjectionOutcome.Ignored, await Apply(app, Snapshot(id, 2, IdentityState.Active)));
        Assert.Equal(IdentityProjectionOutcome.Ignored, await Apply(app, newest));
        Assert.Equal(IdentityProjectionOutcome.Ignored, await Apply(app, newest with { EventId = Guid.NewGuid(), Name = "Changed contact", Email = "changed@example.test" }));
        var conflict = newest with { EventId = Guid.NewGuid(), State = IdentityState.Active };
        Assert.Equal(IdentityProjectionOutcome.Conflict, await Apply(app, conflict));
        Assert.Equal(IdentityProjectionOutcome.Conflict, await Apply(app, conflict));
        Assert.Equal(3L, await app.ScalarAsync<long>("SELECT \"EventVersion\" FROM \"UserAuthorizationStates\""));
        Assert.Equal("Blocked", await app.ScalarAsync<string>("SELECT \"State\" FROM \"UserAuthorizationStates\""));
        Assert.Equal(1L, await app.ScalarAsync<long>("SELECT count(*) FROM \"MessageConflicts\""));
        Assert.Equal(2, await app.ScalarAsync<int>("SELECT \"Observations\" FROM \"MessageConflicts\""));
        var payload = await app.ScalarAsync<string>("SELECT \"Payload\" FROM \"MessageConflicts\"");
        using var json = JsonDocument.Parse(payload);
        Assert.Equal(new[] { "eventVersion", "role", "state", "userId" }, json.RootElement.EnumerateObject().Select(x => x.Name).OrderBy(x => x));
        Assert.DoesNotContain("example.test", payload);
        Assert.Equal(4L, await app.ScalarAsync<long>("SELECT count(*) FROM information_schema.columns WHERE table_name='UserAuthorizationStates'"));
    }

    [Fact]
    public async Task Concurrent_independent_contexts_keep_the_greatest_version()
    {
        await using var app = await CatalogTestApplication.StartAsync(postgres); var id = Guid.NewGuid();
        await Task.WhenAll(Enumerable.Range(1, 12).Select(version => Apply(app, Snapshot(id, version, version == 12 ? IdentityState.Blocked : IdentityState.Active))));
        Assert.Equal(12L, await app.ScalarAsync<long>("SELECT \"EventVersion\" FROM \"UserAuthorizationStates\""));
        Assert.Equal("Blocked", await app.ScalarAsync<string>("SELECT \"State\" FROM \"UserAuthorizationStates\""));
    }

    [Fact]
    public async Task All_three_real_broker_contracts_converge_projection_and_authorization()
    {
        await using var app = await CatalogTestApplication.StartAsync(postgres); var id = Guid.NewGuid();
        var bus = app.Services.GetRequiredService<IBus>(); var initial = Snapshot(id, 1, IdentityState.Active);
        app.Authorize(app.Token("User", id.ToString(), seedProjection: false));
        await bus.Publish(new UserCreatedEvent { EventId = initial.EventId, UserId = id, EventVersion = 1,
            CreatedAtUtc = initial.CreatedAtUtc, OccurredAtUtc = initial.CreatedAtUtc,
            Name = initial.Name, Email = initial.Email, Role = initial.Role, State = initial.State });
        await AwaitVersion(app, 1);
        Assert.Equal(HttpStatusCode.OK, (await app.Client.GetAsync("/api/v1/me/orders")).StatusCode);
        await bus.Publish(new UserStateChangedEvent { EventId = Guid.NewGuid(), UserId = id, EventVersion = 2,
            CreatedAtUtc = initial.CreatedAtUtc, OccurredAtUtc = initial.OccurredAtUtc,
            Name = initial.Name, Email = initial.Email, Role = initial.Role, State = IdentityState.Blocked });
        await AwaitVersion(app, 2);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.Client.GetAsync("/api/v1/me/orders")).StatusCode);
        await bus.Publish(Snapshot(id, 3, IdentityState.Active));
        await AwaitVersion(app, 3);
        Assert.Equal(HttpStatusCode.OK, (await app.Client.GetAsync("/api/v1/me/orders")).StatusCode);
    }

    [Fact]
    public async Task Diagnostic_failure_rolls_back_and_redelivery_records_conflict_before_ack()
    {
        var failure = new FailDiagnosticOnce();
        await using var app = await CatalogTestApplication.StartAsync(postgres, builder => builder.ConfigureTestServices(services =>
            services.AddDbContext<CatalogDbContext>(options => options.AddInterceptors(failure))));
        var id = Guid.NewGuid(); await Apply(app, Snapshot(id, 2, IdentityState.Blocked));
        var divergent = Snapshot(id, 2, IdentityState.Active); failure.Armed = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Apply(app, divergent));
        Assert.True(failure.SawDiagnostic);
        Assert.Equal(0L, await app.ScalarAsync<long>("SELECT count(*) FROM \"MessageConflicts\""));
        Assert.Equal("Blocked", await app.ScalarAsync<string>("SELECT \"State\" FROM \"UserAuthorizationStates\""));
        Assert.Equal(IdentityProjectionOutcome.Conflict, await Apply(app, divergent));
        Assert.Equal(1L, await app.ScalarAsync<long>("SELECT count(*) FROM \"MessageConflicts\""));
    }

    [Fact]
    public async Task Accepted_payment_finishes_even_after_projection_blocks_the_buyer()
    {
        await using var app = await CatalogTestApplication.StartAsync(postgres);
        var order = await Orders.PaymentApplicationTests.Setup(app);
        await Apply(app, Snapshot(order.UserId, 2, IdentityState.Blocked));
        await Orders.PaymentApplicationTests.Apply(app, Orders.PaymentApplicationTests.Payment(order));
        Assert.Equal("Approved", await app.ScalarAsync<string>("SELECT \"Status\" FROM \"Orders\""));
        Assert.Equal(1L, await app.ScalarAsync<long>("SELECT count(*) FROM \"LibraryEntries\""));
        app.Authorize(app.Token());
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.Client.GetAsync("/api/v1/me/library")).StatusCode);
    }

    private sealed class FailDiagnosticOnce : DbTransactionInterceptor
    {
        public bool Armed { get; set; }
        public bool SawDiagnostic { get; private set; }
        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (!Armed) return result;
            await using var command = transaction.Connection!.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT count(*) FROM \"MessageConflicts\" WHERE \"Kind\"='Identity'";
            if ((long)(await command.ExecuteScalarAsync(cancellationToken))! == 0) return result;
            Armed = false; SawDiagnostic = true; throw new InvalidOperationException("Synthetic diagnostic commit failure.");
        }
    }

    internal static UserStateSnapshotEvent Snapshot(Guid id, long version, IdentityState state, IdentityRole role = IdentityRole.User) => new()
    {
        EventId = Guid.NewGuid(), UserId = id, EventVersion = version, Name = "Private identity", Email = "private@example.test",
        CreatedAtUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        OccurredAtUtc = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero), Role = role, State = state
    };
    internal static async Task<IdentityProjectionOutcome> Apply(CatalogTestApplication app, IdentitySnapshot message)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IdentityProjection>().HandleAsync(message, Guid.NewGuid(), CancellationToken.None);
    }
    private static async Task AwaitVersion(CatalogTestApplication app, long version)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (await app.ScalarAsync<long>("SELECT coalesce(max(\"EventVersion\"),0) FROM \"UserAuthorizationStates\"") != version)
            await Task.Delay(50, timeout.Token);
    }
    private sealed class UnavailableReader : IAuthorizationStateReader
    {
        public int Calls { get; private set; }
        public Task<AuthorizationState?> FindAsync(Guid id, CancellationToken token) { Calls++; throw new TimeoutException("fixture storage timeout"); }
    }
    private sealed class AdjustableClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
