using FSH.Framework.Eventing.Outbox;
using FSH.Framework.Eventing.Persistence;

namespace Integration.Tests.Infrastructure;

/// <summary>
/// Drains the outbox synchronously until it stops making progress.
///
/// Publishing via the outbox makes delivery asynchronous — in production the hosted dispatcher
/// picks the row up within <c>OutboxDispatchIntervalSeconds</c> (it is switched off in this host).
/// A test asserting on a consumer's side effect therefore has to drain first.
///
/// It loops rather than dispatching once for two reasons: one cycle claims at most
/// <c>OutboxBatchSize</c> rows, and the whole suite shares one database, so unrelated tests' rows
/// can sit ahead of the one under test in CreatedOnUtc order. Chained events (TenantSubscribed →
/// InvoiceIssued) also need a cycle each. Stops as soon as a pass clears nothing, so rows another
/// test still holds a lease on cannot spin it.
/// </summary>
internal static class OutboxDrain
{
    private const int MaxPasses = 10;

    public static async Task DrainAsync(IServiceProvider services)
    {
        for (int pass = 0; pass < MaxPasses; pass++)
        {
            var before = await CountUnprocessedAsync(services);

            using (var scope = services.CreateScope())
            {
                await scope.ServiceProvider.GetRequiredService<OutboxDispatcher>().DispatchAsync();
            }

            var after = await CountUnprocessedAsync(services);
            if (after >= before)
            {
                return;
            }
        }
    }

    private static async Task<int> CountUnprocessedAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<EventingDbContext>();
        return await context.OutboxMessages.CountAsync(m => m.ProcessedOnUtc == null && !m.IsDead);
    }
}
