using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Contracts.v1.Proxies;
using FSH.Modules.Proxies.Domain;
using FSH.Modules.Proxies.Features.v1.Proxies.RequestProxies;
using FSH.Modules.Proxies.Services;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

namespace Proxies.Tests.Handlers;

public sealed class RequestProxiesHandlerTests
{
    private sealed class PassthroughPasswordResolver : IProxyPasswordResolver
    {
        public string? Decrypt(Proxy proxy) => proxy.ProtectedPassword is null ? null : $"decrypted:{proxy.ProtectedPassword}";
    }

    private static FSH.Modules.Proxies.Data.ProxiesDbContext CreateDb() =>
        Proxies.Tests.TestProxiesDbContext.Create(new DbContextOptionsBuilder<FSH.Modules.Proxies.Data.ProxiesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static HybridCache CreateCache() =>
        new ServiceCollection().AddHybridCache().Services.BuildServiceProvider().GetRequiredService<HybridCache>();

    private static RequestProxiesQueryHandler CreateSut(FSH.Modules.Proxies.Data.ProxiesDbContext db) =>
        new(db, CreateCache(), new PassthroughPasswordResolver());

    private static async Task<(Proxy Matches, Proxy PartialMatch, Proxy Other)> SeedAsync(FSH.Modules.Proxies.Data.ProxiesDbContext db)
    {
        var tagCl = Tag.Create("pais:cl");
        var tagLicitaciones = Tag.Create("funcionalidad:licitaciones");
        var matches = Proxy.Create(ManualProviderAccount.Id, "1.1.1.1", 80, ProxyProtocol.Http, "u", "p", null);
        matches.SetStatus(ProxyStatus.Active);
        matches.AssignTag(tagCl.Id);
        matches.AssignTag(tagLicitaciones.Id);
        var partialMatch = Proxy.Create(ManualProviderAccount.Id, "2.2.2.2", 80, ProxyProtocol.Http, null, null, null);
        partialMatch.SetStatus(ProxyStatus.Active);
        partialMatch.AssignTag(tagCl.Id);
        var other = Proxy.Create(ManualProviderAccount.Id, "3.3.3.3", 80, ProxyProtocol.Http, null, null, null);
        other.SetStatus(ProxyStatus.Active);
        db.Tags.AddRange(tagCl, tagLicitaciones);
        db.Proxies.AddRange(matches, partialMatch, other);
        await db.SaveChangesAsync();
        return (matches, partialMatch, other);
    }

    [Fact]
    public async Task Handle_Should_RequireAllTags_NotAny()
    {
        await using var db = CreateDb();
        var (matches, _, _) = await SeedAsync(db);
        var sut = CreateSut(db);

        var result = await sut.Handle(new RequestProxiesQuery(["pais:cl", "funcionalidad:licitaciones"], 5, ProxySelectionStrategy.Sequential, null), CancellationToken.None);

        result.Select(x => x.Id).ShouldBe([matches.Id]);
    }

    [Fact]
    public async Task Handle_Should_ExcludeInactiveProxies()
    {
        await using var db = CreateDb();
        var tag = Tag.Create("pais:pe");
        var disabled = Proxy.Create(ManualProviderAccount.Id, "9.9.9.9", 80, ProxyProtocol.Http, null, null, null);
        disabled.AssignTag(tag.Id);
        disabled.SetStatus(ProxyStatus.Disabled);
        db.Tags.Add(tag);
        db.Proxies.Add(disabled);
        await db.SaveChangesAsync();
        var sut = CreateSut(db);

        await Should.ThrowAsync<FSH.Framework.Core.Exceptions.NotFoundException>(() =>
            sut.Handle(new RequestProxiesQuery(["pais:pe"], 1, ProxySelectionStrategy.Sequential, null), CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Handle_Should_ReturnDecryptedPassword()
    {
        await using var db = CreateDb();
        await SeedAsync(db);
        var sut = CreateSut(db);

        var result = await sut.Handle(new RequestProxiesQuery(["pais:cl", "funcionalidad:licitaciones"], 1, ProxySelectionStrategy.Sequential, null), CancellationToken.None);

        result.Single().Password.ShouldBe("decrypted:p");
    }

    [Fact]
    public async Task Handle_Should_ReturnSameProxy_ForRepeatedStickySessionCalls()
    {
        await using var db = CreateDb();
        var tag = Tag.Create("pais:cl");
        var a = Proxy.Create(ManualProviderAccount.Id, "1.1.1.1", 80, ProxyProtocol.Http, null, null, null);
        a.SetStatus(ProxyStatus.Active); a.AssignTag(tag.Id);
        var b = Proxy.Create(ManualProviderAccount.Id, "2.2.2.2", 80, ProxyProtocol.Http, null, null, null);
        b.SetStatus(ProxyStatus.Active); b.AssignTag(tag.Id);
        db.Tags.Add(tag);
        db.Proxies.AddRange(a, b);
        await db.SaveChangesAsync();
        var cache = CreateCache();
        var sut = new RequestProxiesQueryHandler(db, cache, new PassthroughPasswordResolver());
        var query = new RequestProxiesQuery(["pais:cl"], 1, ProxySelectionStrategy.Sticky, "session-42");

        var first = await sut.Handle(query, CancellationToken.None);
        var second = await sut.Handle(query, CancellationToken.None);

        first.Single().Id.ShouldBe(second.Single().Id);
    }
}
