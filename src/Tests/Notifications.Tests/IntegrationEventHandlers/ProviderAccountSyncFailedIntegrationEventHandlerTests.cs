using FSH.Modules.Notifications.Domain;
using FSH.Modules.Notifications.IntegrationEventHandlers;
using FSH.Modules.Notifications.Options;
using FSH.Modules.Proxies.Contracts.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Notifications.Tests.IntegrationEventHandlers;

/// <summary>
/// Covers <see cref="ProviderAccountSyncFailedIntegrationEventHandler"/>'s recipient-resolution
/// via <see cref="ProxiesAlertOptions"/> — same design as
/// <see cref="ManualProxyNeedsAttentionIntegrationEventHandlerTests"/>, see that class for the
/// rationale.
/// </summary>
public sealed class ProviderAccountSyncFailedIntegrationEventHandlerTests
{
    private static FSH.Modules.Notifications.Data.NotificationsDbContext CreateDb() =>
        TestNotificationsDbContext.Create(new DbContextOptionsBuilder<FSH.Modules.Notifications.Data.NotificationsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static ProviderAccountSyncFailedIntegrationEvent CreateEvent() => new(
        Guid.CreateVersion7(), DateTime.UtcNow, TenantId: null, Guid.NewGuid().ToString(), "Proxies",
        Guid.CreateVersion7(), "Oxylabs", 3, "401 Unauthorized");

    [Fact]
    public async Task HandleAsync_Should_CreateNotification_When_AdminUserIdConfigured()
    {
        await using var db = CreateDb();
        var options = Options.Create(new ProxiesAlertOptions { AdminUserId = "admin-1" });
        var logger = Substitute.For<ILogger<ProviderAccountSyncFailedIntegrationEventHandler>>();
        var handler = new ProviderAccountSyncFailedIntegrationEventHandler(db, options, logger);
        var @event = CreateEvent();

        await handler.HandleAsync(@event);

        var stored = await db.Notifications.SingleAsync(n => n.UserId == "admin-1");
        stored.Type.ShouldBe("proxies.provider-sync-failed");
        stored.Source.ShouldBe("Proxies");
        stored.Link.ShouldBe($"/proxies/provider-accounts/{@event.ProviderAccountId}");
        stored.Body.ShouldNotBeNull().ShouldContain("401 Unauthorized");
    }

    [Fact]
    public async Task HandleAsync_Should_NotCreateNotification_When_AdminUserIdNotConfigured()
    {
        await using var db = CreateDb();
        var options = Options.Create(new ProxiesAlertOptions { AdminUserId = "  " });
        var logger = Substitute.For<ILogger<ProviderAccountSyncFailedIntegrationEventHandler>>();
        var handler = new ProviderAccountSyncFailedIntegrationEventHandler(db, options, logger);

        await Should.NotThrowAsync(async () => await handler.HandleAsync(CreateEvent()));

        (await db.Notifications.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task HandleAsync_Should_Throw_When_EventIsNull()
    {
        await using var db = CreateDb();
        var options = Options.Create(new ProxiesAlertOptions { AdminUserId = "admin-1" });
        var logger = Substitute.For<ILogger<ProviderAccountSyncFailedIntegrationEventHandler>>();
        var handler = new ProviderAccountSyncFailedIntegrationEventHandler(db, options, logger);

        await Should.ThrowAsync<ArgumentNullException>(async () => await handler.HandleAsync(null!));
    }
}
