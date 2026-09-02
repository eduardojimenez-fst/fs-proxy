using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Eventing.Outbox;
using FSH.Framework.Eventing.Persistence;
using FSH.Framework.Shared.Multitenancy;
using Integration.Tests.Infrastructure;

namespace Integration.Tests.Tests.Eventing;

/// <summary>
/// Covers plan Phase 3: without row claiming, two API instances both drain the same outbox rows
/// and publish every integration event twice. Claiming is a concurrency behaviour, so it needs a
/// real Postgres — SKIP LOCKED has no in-memory equivalent.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class OutboxClaimTests
{
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    private readonly FshWebApplicationFactory _factory;

    public OutboxClaimTests(FshWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Concurrent_Claims_Never_Return_The_Same_Row()
    {
        var marker = $"claim-{Guid.CreateVersion7():N}";
        await SeedAsync(marker, count: 50);

        // Two scopes = two DbContexts = two connections, which is what makes this a real race.
        using var scopeA = await CreateTenantScopeAsync();
        using var scopeB = await CreateTenantScopeAsync();
        var storeA = scopeA.ServiceProvider.GetRequiredService<IOutboxStore>();
        var storeB = scopeB.ServiceProvider.GetRequiredService<IOutboxStore>();

        var results = await Task.WhenAll(
            storeA.ClaimBatchAsync(50, "instance-a", Lease),
            storeB.ClaimBatchAsync(50, "instance-b", Lease));

        var claimedIds = results
            .SelectMany(r => r)
            .Where(m => m.Type == marker)
            .Select(m => m.Id)
            .ToList();

        claimedIds.Distinct().Count().ShouldBe(
            claimedIds.Count,
            "SKIP LOCKED must hand each row to exactly one claimant, or the event is published twice");
        claimedIds.Count.ShouldBe(50, "between them the two instances must still claim every pending row");
    }

    [Fact]
    public async Task Claimed_Rows_Are_Invisible_To_A_Second_Claimant()
    {
        var marker = $"claim-{Guid.CreateVersion7():N}";
        await SeedAsync(marker, count: 5);

        using var scopeA = await CreateTenantScopeAsync();
        var first = await scopeA.ServiceProvider.GetRequiredService<IOutboxStore>()
            .ClaimBatchAsync(500, "instance-a", Lease);
        first.Count(m => m.Type == marker).ShouldBe(5);

        using var scopeB = await CreateTenantScopeAsync();
        var second = await scopeB.ServiceProvider.GetRequiredService<IOutboxStore>()
            .ClaimBatchAsync(500, "instance-b", Lease);

        second.ShouldNotContain(
            m => m.Type == marker,
            "a live lease must hide the row from every other dispatcher");
    }

    [Fact]
    public async Task Expired_Lease_Is_Reclaimable()
    {
        var marker = $"claim-{Guid.CreateVersion7():N}";
        // Lease already in the past: this is the crashed-dispatcher case.
        await SeedAsync(marker, count: 3, claimedUntilUtc: DateTime.UtcNow.AddMinutes(-1), claimedBy: "dead-instance");

        using var scope = await CreateTenantScopeAsync();
        var reclaimed = await scope.ServiceProvider.GetRequiredService<IOutboxStore>()
            .ClaimBatchAsync(500, "instance-b", Lease);

        reclaimed.Count(m => m.Type == marker).ShouldBe(
            3,
            "a crashed dispatcher's rows must be recovered once its lease expires, not stranded forever");
    }

    [Fact]
    public async Task Completing_A_Message_Releases_Its_Lease()
    {
        var marker = $"claim-{Guid.CreateVersion7():N}";
        await SeedAsync(marker, count: 1);

        using var scope = await CreateTenantScopeAsync();
        var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        var context = scope.ServiceProvider.GetRequiredService<EventingDbContext>();

        var claimed = (await store.ClaimBatchAsync(500, "instance-a", Lease)).Single(m => m.Type == marker);
        claimed.ClaimedUntilUtc.ShouldNotBeNull();

        await store.MarkAsFailedAsync(claimed, "boom", isDead: false);

        var stored = await context.OutboxMessages.AsNoTracking().SingleAsync(m => m.Id == claimed.Id);
        stored.ClaimedUntilUtc.ShouldBeNull(
            "a retryable row must not stay hidden behind a stale lease until it expires");
        stored.ClaimedBy.ShouldBeNull();
    }

    private async Task SeedAsync(
        string marker,
        int count,
        DateTime? claimedUntilUtc = null,
        string? claimedBy = null)
    {
        using var scope = await CreateTenantScopeAsync();
        var context = scope.ServiceProvider.GetRequiredService<EventingDbContext>();

        for (int i = 0; i < count; i++)
        {
            context.OutboxMessages.Add(new OutboxMessage
            {
                Id = Guid.CreateVersion7(),
                CreatedOnUtc = DateTime.UtcNow.AddDays(-1).AddMilliseconds(i),
                Type = marker,
                Payload = "{}",
                TenantId = TestConstants.RootTenantId,
                RetryCount = 0,
                IsDead = false,
                ClaimedUntilUtc = claimedUntilUtc,
                ClaimedBy = claimedBy,
            });
        }

        await context.SaveChangesAsync();
    }

    private async Task<IServiceScope> CreateTenantScopeAsync()
    {
        var scope = _factory.Services.CreateScope();
        var tenant = await scope.ServiceProvider
            .GetRequiredService<IMultiTenantStore<AppTenantInfo>>()
            .GetAsync(TestConstants.RootTenantId);
        scope.ServiceProvider.GetRequiredService<IMultiTenantContextSetter>()
            .MultiTenantContext = new MultiTenantContext<AppTenantInfo>(tenant);
        return scope;
    }
}
