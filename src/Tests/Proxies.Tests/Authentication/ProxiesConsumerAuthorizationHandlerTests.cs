using System.Security.Claims;
using FSH.Modules.Identity.Contracts.Services;
using FSH.Modules.Proxies.Authentication;
using FSH.Modules.Proxies.Contracts.Authorization;
using Microsoft.AspNetCore.Authorization;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Proxies.Tests.Authentication;

public sealed class ProxiesConsumerAuthorizationHandlerTests
{
    private const string JwtSchemeName = "Bearer";

    private static AuthorizationHandlerContext CreateContext(ClaimsPrincipal user)
    {
        var requirement = new ProxiesConsumerRequirement();
        return new AuthorizationHandlerContext([requirement], user, resource: null);
    }

    private static ClaimsPrincipal ApiKeyPrincipal() =>
        new(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())],
            ApiKeyAuthenticationDefaults.SchemeName));

    private static ClaimsPrincipal JwtPrincipal(string userId) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], JwtSchemeName));

    [Fact]
    public async Task Should_Succeed_And_SkipPermissionCheck_When_CallerUsedApiKeyScheme()
    {
        var permissions = Substitute.For<IUserPermissionService>();
        var sut = new ProxiesConsumerAuthorizationHandler(permissions);
        var context = CreateContext(ApiKeyPrincipal());

        await sut.HandleAsync(context);

        context.HasSucceeded.ShouldBeTrue();
        await permissions.DidNotReceive()
            .HasPermissionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Should_Succeed_When_JwtCallerHoldsRequestPermission()
    {
        string userId = Guid.NewGuid().ToString();
        var permissions = Substitute.For<IUserPermissionService>();
        permissions.HasPermissionAsync(userId, ProxiesPermissions.Consumers.Request, Arg.Any<CancellationToken>())
            .Returns(true);
        var sut = new ProxiesConsumerAuthorizationHandler(permissions);
        var context = CreateContext(JwtPrincipal(userId));

        await sut.HandleAsync(context);

        context.HasSucceeded.ShouldBeTrue();
    }

    [Fact]
    public async Task Should_NotSucceed_When_JwtCallerLacksRequestPermission()
    {
        string userId = Guid.NewGuid().ToString();
        var permissions = Substitute.For<IUserPermissionService>();
        permissions.HasPermissionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(false);
        var sut = new ProxiesConsumerAuthorizationHandler(permissions);
        var context = CreateContext(JwtPrincipal(userId));

        await sut.HandleAsync(context);

        context.HasSucceeded.ShouldBeFalse();
        await permissions.Received(1)
            .HasPermissionAsync(userId, ProxiesPermissions.Consumers.Request, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Should_NotSucceed_When_PrincipalHasNoNameIdentifierClaim()
    {
        var permissions = Substitute.For<IUserPermissionService>();
        var sut = new ProxiesConsumerAuthorizationHandler(permissions);
        var context = CreateContext(new ClaimsPrincipal(new ClaimsIdentity([], JwtSchemeName)));

        await sut.HandleAsync(context);

        context.HasSucceeded.ShouldBeFalse();
        await permissions.DidNotReceive()
            .HasPermissionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void ConsumerRequestPermission_Should_BeRegistered_AndNotBasic()
    {
        var permission = ProxiesPermissions.All
            .Single(p => p.Resource == ProxiesPermissions.Consumers.Resource);

        permission.Action.ShouldBe("Request");
        permission.IsBasic.ShouldBeFalse();
    }
}
