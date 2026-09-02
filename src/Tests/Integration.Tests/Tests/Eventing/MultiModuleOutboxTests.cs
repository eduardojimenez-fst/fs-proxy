using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Eventing.Inbox;
using FSH.Framework.Eventing.Outbox;
using FSH.Framework.Eventing.Persistence;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.Billing.Contracts.Events;
using FSH.Modules.Identity.Contracts.Events;
using Integration.Tests.Infrastructure;

namespace Integration.Tests.Tests.Eventing;

/// <summary>
/// Covers issue #1349: the outbox was registered once per module DbContext, non-keyed, so .NET DI
/// resolved whichever module registered last for the whole application — and only IdentityDbContext
/// mapped the tables, so every other module's publish hit a missing relation. These tests exercise
/// the real store against Postgres, with no substitution, so they fail if the single framework-owned
/// registration regresses.
/// </summary>
[Collection(FshCollectionDefinition.Name)]
public sealed class MultiModuleOutboxTests
{
    private readonly FshWebApplicationFactory _factory;

    public MultiModuleOutboxTests(FshWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Events_From_Multiple_Modules_Land_In_One_Framework_Outbox()
    {
        using var scope = await CreateTenantScopeAsync();
        var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        var context = scope.ServiceProvider.GetRequiredService<EventingDbContext>();

        var billingEventId = Guid.CreateVersion7();
        var identityEventId = Guid.CreateVersion7();

        // Billing first: under the old registration this is the publish that blew up with
        // "relation billing.OutboxMessages does not exist" — or silently hijacked Identity's store.
        await store.AddAsync(NewBillingEvent(billingEventId));
        await store.AddAsync(NewIdentityEvent(identityEventId));

        var saved = await context.OutboxMessages
            .Where(m => m.Id == billingEventId || m.Id == identityEventId)
            .ToListAsync();

        saved.Select(m => m.Id).ShouldBe([billingEventId, identityEventId], ignoreOrder: true);
        saved.ShouldContain(
            m => m.Type.Contains(nameof(InvoiceIssuedIntegrationEvent), StringComparison.Ordinal),
            "a non-Identity module's event must be persisted, not dropped");
        saved.ShouldAllBe(m => m.ProcessedOnUtc == null && !m.IsDead);
    }

    [Fact]
    public async Task Application_Registers_Exactly_One_Outbox_And_Inbox_Store()
    {
        using var scope = await CreateTenantScopeAsync();

        scope.ServiceProvider.GetServices<IOutboxStore>().Count().ShouldBe(
            1,
            "a second IOutboxStore registration makes DI resolve the last one for every module (issue #1349)");
        scope.ServiceProvider.GetServices<IInboxStore>().Count().ShouldBe(
            1,
            "a second IInboxStore registration silently redirects idempotency writes (issue #1349)");
    }

    [Fact]
    public async Task Inbox_Idempotency_Writes_Land_In_The_Framework_Schema()
    {
        using var scope = await CreateTenantScopeAsync();
        var inbox = scope.ServiceProvider.GetRequiredService<IInboxStore>();
        var context = scope.ServiceProvider.GetRequiredService<EventingDbContext>();
        var eventId = Guid.CreateVersion7();

        (await inbox.HasProcessedAsync(eventId, "MultiModuleOutboxTests")).ShouldBeFalse();

        await inbox.MarkProcessedAsync(
            eventId,
            "MultiModuleOutboxTests",
            TestConstants.RootTenantId,
            nameof(InvoiceIssuedIntegrationEvent));

        (await inbox.HasProcessedAsync(eventId, "MultiModuleOutboxTests")).ShouldBeTrue();
        (await context.InboxMessages.CountAsync(m => m.Id == eventId)).ShouldBe(1);
    }

    [Fact]
    public async Task Identity_No_Longer_Owns_The_Outbox_Tables()
    {
        using var scope = await CreateTenantScopeAsync();
        var identity = scope.ServiceProvider.GetRequiredService<FSH.Modules.Identity.Data.IdentityDbContext>();

        identity.Model.FindEntityType(typeof(OutboxMessage)).ShouldBeNull(
            "the outbox is framework infrastructure; a module mapping it again reintroduces the ambiguity");
        identity.Model.FindEntityType(typeof(InboxMessage)).ShouldBeNull();
    }

    private static InvoiceIssuedIntegrationEvent NewBillingEvent(Guid id) => new(
        id,
        DateTime.UtcNow,
        TestConstants.RootTenantId,
        $"corr-{id:N}",
        "Billing",
        Guid.CreateVersion7(),
        "INV-1349",
        42.00m,
        "USD",
        DateTime.UtcNow.AddDays(14),
        2026,
        8);

    private static UserRegisteredIntegrationEvent NewIdentityEvent(Guid id) => new(
        id,
        DateTime.UtcNow,
        TestConstants.RootTenantId,
        $"corr-{id:N}",
        "Identity",
        Guid.CreateVersion7().ToString(),
        "outbox-1349@fullstackhero.net",
        "Out",
        "Box");

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
