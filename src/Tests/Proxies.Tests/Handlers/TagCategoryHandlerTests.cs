using FSH.Framework.Core.Exceptions;
using FSH.Modules.Proxies.Contracts.v1.TagCategories;
using FSH.Modules.Proxies.Data;
using FSH.Modules.Proxies.Domain;
using FSH.Modules.Proxies.Features.v1.TagCategories.AddTagCategoryValue;
using FSH.Modules.Proxies.Features.v1.TagCategories.CreateTagCategory;
using FSH.Modules.Proxies.Features.v1.TagCategories.DeleteTagCategory;
using FSH.Modules.Proxies.Features.v1.TagCategories.ListTagCategories;
using FSH.Modules.Proxies.Features.v1.TagCategories.RemoveTagCategoryValue;
using FSH.Modules.Proxies.Features.v1.TagCategories.UpdateTagCategory;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

namespace Proxies.Tests.Handlers;

public sealed class TagCategoryHandlerTests
{
    private static ProxiesDbContext CreateDb() =>
        Proxies.Tests.TestProxiesDbContext.Create(new DbContextOptionsBuilder<ProxiesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    [Fact]
    public async Task Create_Should_Persist()
    {
        await using var db = CreateDb();
        var sut = new CreateTagCategoryCommandHandler(db);

        var id = await sut.Handle(new CreateTagCategoryCommand("pais"), CancellationToken.None);

        (await db.TagCategories.SingleAsync(x => x.Id == id)).Name.ShouldBe("pais");
    }

    [Fact]
    public async Task Update_Should_Rename()
    {
        await using var db = CreateDb();
        var category = TagCategory.Create("pais");
        db.TagCategories.Add(category);
        await db.SaveChangesAsync();
        var sut = new UpdateTagCategoryCommandHandler(db);

        await sut.Handle(new UpdateTagCategoryCommand(category.Id, "country"), CancellationToken.None);

        (await db.TagCategories.SingleAsync(x => x.Id == category.Id)).Name.ShouldBe("country");
    }

    [Fact]
    public async Task Delete_Should_Remove()
    {
        await using var db = CreateDb();
        var category = TagCategory.Create("pais");
        db.TagCategories.Add(category);
        await db.SaveChangesAsync();
        var sut = new DeleteTagCategoryCommandHandler(db);

        await sut.Handle(new DeleteTagCategoryCommand(category.Id), CancellationToken.None);

        (await db.TagCategories.AnyAsync(x => x.Id == category.Id)).ShouldBeFalse();
    }

    [Fact]
    public async Task Delete_Should_Throw_When_NotFound()
    {
        await using var db = CreateDb();
        var sut = new DeleteTagCategoryCommandHandler(db);

        await Should.ThrowAsync<NotFoundException>(() => sut.Handle(new DeleteTagCategoryCommand(Guid.NewGuid()), CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task AddValue_Should_Append()
    {
        await using var db = CreateDb();
        var category = TagCategory.Create("pais");
        db.TagCategories.Add(category);
        await db.SaveChangesAsync();
        var sut = new AddTagCategoryValueCommandHandler(db);

        await sut.Handle(new AddTagCategoryValueCommand(category.Id, "cl"), CancellationToken.None);

        var reloaded = await db.TagCategories.Include(x => x.Values).SingleAsync(x => x.Id == category.Id);
        reloaded.Values.Select(v => v.Value).ShouldBe(["cl"]);
    }

    [Fact]
    public async Task RemoveValue_Should_Remove()
    {
        await using var db = CreateDb();
        var category = TagCategory.Create("pais");
        category.AddValue("cl");
        db.TagCategories.Add(category);
        await db.SaveChangesAsync();
        var sut = new RemoveTagCategoryValueCommandHandler(db);

        await sut.Handle(new RemoveTagCategoryValueCommand(category.Id, "cl"), CancellationToken.None);

        var reloaded = await db.TagCategories.Include(x => x.Values).SingleAsync(x => x.Id == category.Id);
        reloaded.Values.ShouldBeEmpty();
    }

    [Fact]
    public async Task List_Should_ReturnCategoriesWithValues_OrderedByName()
    {
        await using var db = CreateDb();
        var funcionalidad = TagCategory.Create("funcionalidad");
        funcionalidad.AddValue("licitaciones");
        var pais = TagCategory.Create("pais");
        pais.AddValue("cl");
        pais.AddValue("ar");
        db.TagCategories.AddRange(funcionalidad, pais);
        await db.SaveChangesAsync();
        var sut = new ListTagCategoriesQueryHandler(db);

        var result = await sut.Handle(new ListTagCategoriesQuery(), CancellationToken.None);

        result.Select(x => x.Name).ShouldBe(["funcionalidad", "pais"]);
        result.Single(x => x.Name == "pais").Values.ShouldBe(["ar", "cl"]);
    }
}
