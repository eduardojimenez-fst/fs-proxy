using FSH.Modules.Proxies.Domain;
using Shouldly;
using Xunit;

namespace Proxies.Tests.Domain;

public sealed class PolicyProfileTests
{
    [Theory]
    [InlineData(PolicyProfileType.Manual, 0)]
    [InlineData(PolicyProfileType.AutoDisable, 1)]
    [InlineData(PolicyProfileType.AutoDisableAndRenew, 2)]
    public void RestrictivenessRank_Should_OrderByType(PolicyProfileType type, int expectedRank)
    {
        var profile = PolicyProfile.Create("test", type, failureThreshold: 3, windowMinutes: 30, minDistinctReporters: 2);

        profile.RestrictivenessRank.ShouldBe(expectedRank);
    }
}
