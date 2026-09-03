using FSH.Modules.Proxies.Contracts.v1.HealthCheckTargets;
using FSH.Modules.Proxies.Data;
using FSH.Modules.Proxies.Domain;
using FSH.Modules.Proxies.Features.v1.HealthCheckTargets.AssignHealthCheckTargetToTag;
using FSH.Modules.Proxies.Features.v1.HealthCheckTargets.CreateHealthCheckTarget;
using FSH.Modules.Proxies.Features.v1.HealthCheckTargets.UnassignHealthCheckTargetFromTag;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

namespace Proxies.Tests.Handlers;

public sealed class HealthCheckTargetHandlerTests
{
    private static ProxiesDbContext CreateDb() =>
        TestProxiesDbContext.Create(new DbContextOptionsBuilder<ProxiesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    [Fact]
    public async Task Create_Should_Persist()
    {
        await using var db = CreateDb();
        var sut = new CreateHealthCheckTargetCommandHandler(db);

        var id = await sut.Handle(new CreateHealthCheckTargetCommand("Mercado Publico", "https://www.mercadopublico.cl", 200, null, 5000), CancellationToken.None);

        (await db.HealthCheckTargets.SingleAsync(x => x.Id == id)).TestUrl.ShouldBe("https://www.mercadopublico.cl");
    }

    [Fact]
    public async Task AssignToTag_Should_ReplaceExistingAssignment()
    {
        await using var db = CreateDb();
        var tag = Tag.Create("pais:cl");
        var targetA = HealthCheckTarget.Create("a", "https://a.example", 200, null, 5000);
        var targetB = HealthCheckTarget.Create("b", "https://b.example", 200, null, 5000);
        db.Tags.Add(tag);
        db.HealthCheckTargets.AddRange(targetA, targetB);
        await db.SaveChangesAsync();
        var sut = new AssignHealthCheckTargetToTagCommandHandler(db);
        await sut.Handle(new AssignHealthCheckTargetToTagCommand(tag.Id, targetA.Id), CancellationToken.None);

        await sut.Handle(new AssignHealthCheckTargetToTagCommand(tag.Id, targetB.Id), CancellationToken.None);

        var assignment = await db.Set<TagHealthCheckTargetAssignment>().SingleAsync(x => x.TagId == tag.Id);
        assignment.HealthCheckTargetId.ShouldBe(targetB.Id);
    }

    [Fact]
    public async Task Unassign_Should_RemoveAssignment()
    {
        await using var db = CreateDb();
        var tag = Tag.Create("pais:pe");
        var target = HealthCheckTarget.Create("a", "https://a.example", 200, null, 5000);
        db.Tags.Add(tag);
        db.HealthCheckTargets.Add(target);
        db.Set<TagHealthCheckTargetAssignment>().Add(TagHealthCheckTargetAssignment.Create(tag.Id, target.Id));
        await db.SaveChangesAsync();
        var sut = new UnassignHealthCheckTargetFromTagCommandHandler(db);

        await sut.Handle(new UnassignHealthCheckTargetFromTagCommand(tag.Id), CancellationToken.None);

        (await db.Set<TagHealthCheckTargetAssignment>().AnyAsync(x => x.TagId == tag.Id)).ShouldBeFalse();
    }
}
