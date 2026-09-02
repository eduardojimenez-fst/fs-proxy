using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Contracts.v1.ManualProxies;
using FSH.Modules.Proxies.Features.v1.ManualProxies.CreateManualProxy;
using Shouldly;
using Xunit;

namespace Proxies.Tests.Validators;

public sealed class ManualProxyValidatorTests
{
    private readonly CreateManualProxyCommandValidator _validator = new();

    [Fact]
    public void Should_Pass_When_Valid()
    {
        var command = new CreateManualProxyCommand("10.0.0.5", 3128, ProxyProtocol.Http, "user", "pass", ["pais:cl"]);

        _validator.Validate(command).IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public void Should_Fail_When_PortOutOfRange(int port)
    {
        var command = new CreateManualProxyCommand("10.0.0.5", port, ProxyProtocol.Http, null, null, []);

        _validator.Validate(command).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void Should_Fail_When_HostEmpty()
    {
        var command = new CreateManualProxyCommand("", 3128, ProxyProtocol.Http, null, null, []);

        _validator.Validate(command).IsValid.ShouldBeFalse();
    }
}
