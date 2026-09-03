using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Contracts.v1.Policies;
using FSH.Modules.Proxies.Data;
using FSH.Modules.Proxies.Domain;
using FSH.Modules.Proxies.Features.v1.Policies.AssignPolicyToTag;
using FSH.Modules.Proxies.Features.v1.Policies.CreatePolicyProfile;
using FSH.Modules.Proxies.Features.v1.Policies.UnassignPolicyFromTag;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

namespace Proxies.Tests.Handlers;

public sealed class PolicyProfileHandlerTests
{
    private static ProxiesDbContext CreateDb() =>
        TestProxiesDbContext.Create(new DbContextOptionsBuilder<ProxiesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    [Fact]
    public async Task Create_Should_Persist()
    {
        await using var db = CreateDb();
        var sut = new CreatePolicyProfileCommandHandler(db);

        var id = await sut.Handle(new CreatePolicyProfileCommand("critical", PolicyProfileType.AutoDisableAndRenew, 3, 30, 2), CancellationToken.None);

        (await db.PolicyProfiles.SingleAsync(x => x.Id == id)).Type.ShouldBe(PolicyProfileType.AutoDisableAndRenew);
    }

    [Fact]
    public async Task AssignToTag_Should_ReplaceExistingAssignment()
    {
        await using var db = CreateDb();
        var tag = Tag.Create("pais:cl");
        var policyA = PolicyProfile.Create("a", PolicyProfileType.Manual, 1, 1, 1);
        var policyB = PolicyProfile.Create("b", PolicyProfileType.AutoDisable, 3, 30, 2);
        db.Tags.Add(tag);
        db.PolicyProfiles.AddRange(policyA, policyB);
        await db.SaveChangesAsync();
        var sut = new AssignPolicyToTagCommandHandler(db);
        await sut.Handle(new AssignPolicyToTagCommand(tag.Id, policyA.Id), CancellationToken.None);

        await sut.Handle(new AssignPolicyToTagCommand(tag.Id, policyB.Id), CancellationToken.None);

        var assignment = await db.Set<TagPolicyAssignment>().SingleAsync(x => x.TagId == tag.Id);
        assignment.PolicyProfileId.ShouldBe(policyB.Id);
    }

    [Fact]
    public async Task Unassign_Should_RemoveAssignment()
    {
        await using var db = CreateDb();
        var tag = Tag.Create("pais:pe");
        var policy = PolicyProfile.Create("a", PolicyProfileType.Manual, 1, 1, 1);
        db.Tags.Add(tag);
        db.PolicyProfiles.Add(policy);
        db.Set<TagPolicyAssignment>().Add(TagPolicyAssignment.Create(tag.Id, policy.Id));
        await db.SaveChangesAsync();
        var sut = new UnassignPolicyFromTagCommandHandler(db);

        await sut.Handle(new UnassignPolicyFromTagCommand(tag.Id), CancellationToken.None);

        (await db.Set<TagPolicyAssignment>().AnyAsync(x => x.TagId == tag.Id)).ShouldBeFalse();
    }
}
