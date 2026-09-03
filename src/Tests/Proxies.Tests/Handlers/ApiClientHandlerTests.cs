using FSH.Modules.Proxies.Contracts.v1.ApiClients;
using FSH.Modules.Proxies.Domain;
using FSH.Modules.Proxies.Features.v1.ApiClients.CreateApiClient;
using FSH.Modules.Proxies.Features.v1.ApiClients.DeleteApiClient;
using FSH.Modules.Proxies.Services;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

namespace Proxies.Tests.Handlers;

public sealed class ApiClientHandlerTests
{
    private static FSH.Modules.Proxies.Data.ProxiesDbContext CreateDb() =>
        Proxies.Tests.TestProxiesDbContext.Create(new DbContextOptionsBuilder<FSH.Modules.Proxies.Data.ProxiesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    [Fact]
    public async Task Create_Should_StoreOnlyTheHash_And_ReturnThePlaintextKeyOnce()
    {
        await using var db = CreateDb();
        var sut = new CreateApiClientCommandHandler(db, new ApiKeyHasher());

        var result = await sut.Handle(new CreateApiClientCommand("TAG"), CancellationToken.None);

        var stored = await db.ApiClients.SingleAsync(x => x.Id == result.Id);
        stored.ApiKeyHash.ShouldNotBe(result.PlaintextKey);
        stored.ApiKeyHash.ShouldBe(new ApiKeyHasher().Hash(result.PlaintextKey));
    }

    [Fact]
    public async Task Delete_Should_RemoveApiClient()
    {
        await using var db = CreateDb();
        var client = ApiClient.Create("legacy-scraper", "hash");
        db.ApiClients.Add(client);
        await db.SaveChangesAsync();
        var sut = new DeleteApiClientCommandHandler(db);

        await sut.Handle(new DeleteApiClientCommand(client.Id), CancellationToken.None);

        (await db.ApiClients.AnyAsync(x => x.Id == client.Id)).ShouldBeFalse();
    }
}
