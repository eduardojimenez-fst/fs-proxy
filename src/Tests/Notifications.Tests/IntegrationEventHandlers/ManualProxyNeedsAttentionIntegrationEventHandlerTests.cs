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
/// Covers <see cref="ManualProxyNeedsAttentionIntegrationEventHandler"/>'s recipient-resolution
/// via <see cref="ProxiesAlertOptions"/> (Task 17): a configured <c>AdminUserId</c> gets a real
/// <see cref="Notification"/> row; an unconfigured one is a graceful no-op (log + drop, no throw).
/// </summary>
public sealed class ManualProxyNeedsAttentionIntegrationEventHandlerTests
{
    private static FSH.Modules.Notifications.Data.NotificationsDbContext CreateDb() =>
        TestNotificationsDbContext.Create(new DbContextOptionsBuilder<FSH.Modules.Notifications.Data.NotificationsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static ManualProxyNeedsAttentionIntegrationEvent CreateEvent() => new(
        Guid.CreateVersion7(), DateTime.UtcNow, TenantId: null, Guid.NewGuid().ToString(), "Proxies",
        Guid.CreateVersion7(), "10.0.0.1:8080");

    [Fact]
    public async Task HandleAsync_Should_CreateNotification_When_AdminUserIdConfigured()
    {
        await using var db = CreateDb();
        var options = Options.Create(new ProxiesAlertOptions { AdminUserId = "admin-1" });
        var logger = Substitute.For<ILogger<ManualProxyNeedsAttentionIntegrationEventHandler>>();
        var handler = new ManualProxyNeedsAttentionIntegrationEventHandler(db, options, logger);
        var @event = CreateEvent();

        await handler.HandleAsync(@event);

        var stored = await db.Notifications.SingleAsync(n => n.UserId == "admin-1");
        stored.Type.ShouldBe("proxies.manual-needs-attention");
        stored.Source.ShouldBe("Proxies");
        stored.Link.ShouldBe($"/proxies?highlight={@event.ProxyId}");
        stored.Body.ShouldNotBeNull().ShouldContain(@event.Host);
    }

    [Fact]
    public async Task HandleAsync_Should_NotCreateNotification_When_AdminUserIdNotConfigured()
    {
        await using var db = CreateDb();
        var options = Options.Create(new ProxiesAlertOptions { AdminUserId = null });
        var logger = Substitute.For<ILogger<ManualProxyNeedsAttentionIntegrationEventHandler>>();
        var handler = new ManualProxyNeedsAttentionIntegrationEventHandler(db, options, logger);

        await Should.NotThrowAsync(async () => await handler.HandleAsync(CreateEvent()));

        (await db.Notifications.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task HandleAsync_Should_Throw_When_EventIsNull()
    {
        await using var db = CreateDb();
        var options = Options.Create(new ProxiesAlertOptions { AdminUserId = "admin-1" });
        var logger = Substitute.For<ILogger<ManualProxyNeedsAttentionIntegrationEventHandler>>();
        var handler = new ManualProxyNeedsAttentionIntegrationEventHandler(db, options, logger);

        await Should.ThrowAsync<ArgumentNullException>(async () => await handler.HandleAsync(null!));
    }
}
