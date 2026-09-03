using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Domain;
using FSH.Modules.Proxies.Options;
using FSH.Modules.Proxies.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Proxies.Tests.Services;

public sealed class HealthCheckTargetResolverTests
{
    private static FSH.Modules.Proxies.Data.ProxiesDbContext CreateDb() =>
        TestProxiesDbContext.Create(new DbContextOptionsBuilder<FSH.Modules.Proxies.Data.ProxiesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static IOptions<ProxiesOptions> DefaultOptions() => Options.Create(new ProxiesOptions());

    [Fact]
    public async Task ResolveTargetsAsync_Should_ReturnDistinctTargets_FromProxyTags()
    {
        await using var db = CreateDb();
        var tagCl = Tag.Create("pais:cl");
        var tagLicitaciones = Tag.Create("funcionalidad:licitaciones");
        var mercadoPublico = HealthCheckTarget.Create("Mercado Publico", "https://www.mercadopublico.cl", 200, null, 5000);
        var proxy = Proxy.Create(ManualProviderAccount.Id, "1.1.1.1", 80, ProxyProtocol.Http, null, null, null);
        proxy.AssignTag(tagCl.Id);
        proxy.AssignTag(tagLicitaciones.Id);
        db.Tags.AddRange(tagCl, tagLicitaciones);
        db.HealthCheckTargets.Add(mercadoPublico);
        db.Proxies.Add(proxy);
        db.Set<TagHealthCheckTargetAssignment>().Add(TagHealthCheckTargetAssignment.Create(tagCl.Id, mercadoPublico.Id));
        await db.SaveChangesAsync();
        var sut = new HealthCheckTargetResolver(db, DefaultOptions());

        var result = await sut.ResolveTargetsAsync(proxy.Id, CancellationToken.None);

        result.ShouldHaveSingleItem();
        result[0].TestUrl.ShouldBe("https://www.mercadopublico.cl");
        result[0].TargetId.ShouldBe(mercadoPublico.Id);
    }

    [Fact]
    public async Task ResolveTargetsAsync_Should_FallBackToGlobalDefault_When_NoTagHasATarget()
    {
        await using var db = CreateDb();
        var proxy = Proxy.Create(ManualProviderAccount.Id, "1.1.1.1", 80, ProxyProtocol.Http, null, null, null);
        db.Proxies.Add(proxy);
        await db.SaveChangesAsync();
        var sut = new HealthCheckTargetResolver(db, DefaultOptions());

        var result = await sut.ResolveTargetsAsync(proxy.Id, CancellationToken.None);

        result.ShouldHaveSingleItem();
        result[0].TargetId.ShouldBeNull();
        result[0].TestUrl.ShouldBe("https://www.google.com/generate_204");
    }
}
