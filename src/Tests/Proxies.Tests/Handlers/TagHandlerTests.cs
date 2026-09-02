using FSH.Modules.Proxies.Contracts.v1.Tags;
using FSH.Modules.Proxies.Domain;
using FSH.Modules.Proxies.Features.v1.Tags.CreateTag;
using FSH.Modules.Proxies.Features.v1.Tags.DeleteTag;
using FSH.Modules.Proxies.Features.v1.Tags.ListTags;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

namespace Proxies.Tests.Handlers;

public sealed class TagHandlerTests
{
    private static FSH.Modules.Proxies.Data.ProxiesDbContext CreateDb() =>
        Proxies.Tests.TestProxiesDbContext.Create(new DbContextOptionsBuilder<FSH.Modules.Proxies.Data.ProxiesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    [Fact]
    public async Task Create_Should_NormalizeName()
    {
        await using var db = CreateDb();
        var sut = new CreateTagCommandHandler(db);

        var id = await sut.Handle(new CreateTagCommand("  PAIS:CL  "), CancellationToken.None);

        (await db.Tags.SingleAsync(x => x.Id == id)).Name.ShouldBe("pais:cl");
    }

    [Fact]
    public async Task Delete_Should_RemoveTag()
    {
        await using var db = CreateDb();
        var tag = Tag.Create("pais:pe");
        db.Tags.Add(tag);
        await db.SaveChangesAsync();
        var sut = new DeleteTagCommandHandler(db);

        await sut.Handle(new DeleteTagCommand(tag.Id), CancellationToken.None);

        (await db.Tags.AnyAsync(x => x.Id == tag.Id)).ShouldBeFalse();
    }

    [Fact]
    public async Task List_Should_ReturnAllTags_OrderedByName()
    {
        await using var db = CreateDb();
        db.Tags.AddRange(Tag.Create("zeta"), Tag.Create("alpha"));
        await db.SaveChangesAsync();
        var sut = new ListTagsQueryHandler(db);

        var result = await sut.Handle(new ListTagsQuery(), CancellationToken.None);

        result.Select(x => x.Name).ShouldBe(["alpha", "zeta"]);
    }
}
