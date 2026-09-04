using FSH.Modules.Proxies.Contracts.v1.Proxies;
using FSH.Modules.Proxies.Features.v1.Proxies.AssignProxyTag;
using FSH.Modules.Proxies.Features.v1.Proxies.SetProxyTags;
using FSH.Modules.Proxies.Features.v1.Proxies.UnassignProxyTag;
using Shouldly;
using Xunit;

namespace Proxies.Tests.Validators;

public sealed class ProxyTagAssignmentValidatorTests
{
    private readonly SetProxyTagsCommandValidator _setValidator = new();
    private readonly AssignProxyTagCommandValidator _assignValidator = new();
    private readonly UnassignProxyTagCommandValidator _unassignValidator = new();

    [Fact]
    public void SetProxyTags_Should_Pass_When_Valid()
    {
        var command = new SetProxyTagsCommand(Guid.NewGuid(), ["pais:cl", "funcionalidad:licitaciones"]);

        _setValidator.Validate(command).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void SetProxyTags_Should_Fail_When_TagNameExceedsMaxLength()
    {
        var tooLong = new string('a', 129);
        var command = new SetProxyTagsCommand(Guid.NewGuid(), [tooLong]);

        _setValidator.Validate(command).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void SetProxyTags_Should_Pass_When_TagNameAtMaxLength()
    {
        var atLimit = new string('a', 128);
        var command = new SetProxyTagsCommand(Guid.NewGuid(), [atLimit]);

        _setValidator.Validate(command).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void AssignProxyTag_Should_Fail_When_TagNameExceedsMaxLength()
    {
        var tooLong = new string('a', 129);
        var command = new AssignProxyTagCommand([Guid.NewGuid()], tooLong);

        _assignValidator.Validate(command).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void AssignProxyTag_Should_Pass_When_TagNameAtMaxLength()
    {
        var atLimit = new string('a', 128);
        var command = new AssignProxyTagCommand([Guid.NewGuid()], atLimit);

        _assignValidator.Validate(command).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void UnassignProxyTag_Should_Fail_When_TagNameExceedsMaxLength()
    {
        var tooLong = new string('a', 129);
        var command = new UnassignProxyTagCommand([Guid.NewGuid()], tooLong);

        _unassignValidator.Validate(command).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void UnassignProxyTag_Should_Pass_When_TagNameAtMaxLength()
    {
        var atLimit = new string('a', 128);
        var command = new UnassignProxyTagCommand([Guid.NewGuid()], atLimit);

        _unassignValidator.Validate(command).IsValid.ShouldBeTrue();
    }
}
