using System.Diagnostics.CodeAnalysis;
using FSH.Framework.Core.Exceptions;
using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Contracts.Dtos;
using FSH.Modules.Proxies.Contracts.v1.Proxies;
using FSH.Modules.Proxies.Data;
using FSH.Modules.Proxies.Domain;
using FSH.Modules.Proxies.Services;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;

namespace FSH.Modules.Proxies.Features.v1.Proxies.RequestProxies;

// Note: the design draft this handler was built from also threaded an unused IOptions<ProxiesOptions>
// parameter through the constructor (presumably scaffolding for a future configurable knob, e.g. a
// selection-strategy default or a max-count ceiling). Nothing in this task's scope reads it — Count is
// hard-capped by RequestProxiesQueryValidator instead — so it was dropped rather than kept as dead,
// CS9113-triggering ("parameter is unread") ballast; a later task can reintroduce it once there's an
// actual setting to bind.
public sealed class RequestProxiesQueryHandler(
    ProxiesDbContext dbContext, HybridCache cache, IProxyPasswordResolver passwordResolver)
    : IQueryHandler<RequestProxiesQuery, IReadOnlyList<ProxyConnectionDto>>
{
    public async ValueTask<IReadOnlyList<ProxyConnectionDto>> Handle(RequestProxiesQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var candidates = await ResolveCandidatesAsync(query.Tags, cancellationToken).ConfigureAwait(false);
        if (candidates.Count == 0)
        {
            throw new NotFoundException("No active proxies match the requested tags.");
        }

        List<Proxy> selected = query.Strategy switch
        {
            ProxySelectionStrategy.Sticky => [await ResolveStickyAsync(query, candidates, cancellationToken).ConfigureAwait(false)],
            ProxySelectionStrategy.Random => ResolveRandom(candidates, query.Count),
            ProxySelectionStrategy.Sequential => [.. candidates.Take(query.Count)],
            _ => await ResolveRoundRobinAsync(query.Tags, candidates, query.Count, cancellationToken).ConfigureAwait(false),
        };

        return [.. selected.Select(p => new ProxyConnectionDto(p.Id, p.Host, p.Port, p.Protocol, p.Username, passwordResolver.Decrypt(p)))];
    }

    // Shuffle-and-take for load spreading across scrapers, not a security decision, so the
    // shared, non-cryptographic PRNG is the right (and cheaper) tool — CA5394 does not apply.
    [SuppressMessage("Security", "CA5394:Do not use insecure randomness",
        Justification = "Random proxy selection is a load-distribution convenience, not a security " +
            "control — Random.Shared is appropriate here; CA5394's cryptographic-randomness concern " +
            "does not apply.")]
    private static List<Proxy> ResolveRandom(List<Proxy> candidates, int count) =>
        [.. candidates.OrderBy(_ => Random.Shared.Next()).Take(count)];

    private async Task<List<Proxy>> ResolveCandidatesAsync(IReadOnlyList<string> tags, CancellationToken cancellationToken)
    {
        var query = dbContext.Proxies.Where(p => p.Status == ProxyStatus.Active);

        foreach (var tagName in tags.Select(Tag.Normalize).Distinct())
        {
            var proxyIdsWithThisTag = dbContext.Tags.Where(t => t.Name == tagName)
                .Join(dbContext.Set<ProxyTagAssignment>(), t => t.Id, a => a.TagId, (t, a) => a.ProxyId);
            query = query.Where(p => proxyIdsWithThisTag.Contains(p.Id));
        }

        return await query.OrderBy(p => p.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<Proxy> ResolveStickyAsync(RequestProxiesQuery query, List<Proxy> candidates, CancellationToken cancellationToken)
    {
        string cacheKey = $"proxies:session:{query.SessionId}:{string.Join(',', query.Tags.Select(Tag.Normalize).OrderBy(t => t))}";

        var cachedId = await cache.GetOrCreateAsync(cacheKey, candidates,
            (state, _) => ValueTask.FromResult(state[0].Id),
            new HybridCacheEntryOptions { Expiration = TimeSpan.FromMinutes(30), LocalCacheExpiration = TimeSpan.FromMinutes(2) },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var stillActive = candidates.FirstOrDefault(p => p.Id == cachedId);
        if (stillActive is not null) return stillActive;

        // The cached proxy is no longer in the active candidate set (disabled/retired since last pinned) — evict and re-pick.
        await cache.RemoveAsync(cacheKey, cancellationToken).ConfigureAwait(false);
        var freshId = await cache.GetOrCreateAsync(cacheKey, candidates,
            (state, _) => ValueTask.FromResult(state[0].Id),
            new HybridCacheEntryOptions { Expiration = TimeSpan.FromMinutes(30), LocalCacheExpiration = TimeSpan.FromMinutes(2) },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return candidates.First(p => p.Id == freshId);
    }

    private async Task<List<Proxy>> ResolveRoundRobinAsync(IReadOnlyList<string> tags, List<Proxy> candidates, int count, CancellationToken cancellationToken)
    {
        string cursorKey = $"proxies:round-robin-cursor:{string.Join(',', tags.Select(Tag.Normalize).OrderBy(t => t))}";
        int cursor = await cache.GetOrCreateAsync(cursorKey, 0, (seed, _) => ValueTask.FromResult(seed),
            new HybridCacheEntryOptions { Expiration = TimeSpan.FromDays(1) }, cancellationToken: cancellationToken).ConfigureAwait(false);

        var result = new List<Proxy>(Math.Min(count, candidates.Count));
        for (int i = 0; i < count && i < candidates.Count; i++)
        {
            result.Add(candidates[(cursor + i) % candidates.Count]);
        }

        await cache.RemoveAsync(cursorKey, cancellationToken).ConfigureAwait(false);
        await cache.GetOrCreateAsync(cursorKey, cursor + result.Count, (seed, _) => ValueTask.FromResult(seed),
            new HybridCacheEntryOptions { Expiration = TimeSpan.FromDays(1) }, cancellationToken: cancellationToken).ConfigureAwait(false);

        return result;
    }
}
