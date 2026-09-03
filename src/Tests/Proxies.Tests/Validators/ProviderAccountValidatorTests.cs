using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Contracts.v1.ProviderAccounts;
using FSH.Modules.Proxies.Features.v1.ProviderAccounts.CreateProviderAccount;
using Shouldly;
using Xunit;

namespace Proxies.Tests.Validators;

public sealed class ProviderAccountValidatorTests
{
    private readonly CreateProviderAccountCommandValidator _validator = new();

    [Fact]
    public void Should_Pass_When_Valid()
    {
        var command = new CreateProviderAccountCommand("WebShare - main", ProxyProviderType.WebShare, "api-key-123");

        _validator.Validate(command).IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Should_Fail_When_NameEmpty(string name)
    {
        var command = new CreateProviderAccountCommand(name, ProxyProviderType.WebShare, "api-key-123");

        _validator.Validate(command).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void Should_Fail_When_CredentialsEmpty()
    {
        var command = new CreateProviderAccountCommand("WebShare - main", ProxyProviderType.WebShare, "");

        _validator.Validate(command).IsValid.ShouldBeFalse();
    }
}
