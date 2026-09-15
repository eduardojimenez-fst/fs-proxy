using FSH.Modules.Proxies.Contracts;
using FSH.Proxy.Client;
using Shouldly;
using Xunit;

namespace Proxy.Client.Tests;

public sealed class ProxyOutcomeTests
{
    [Fact]
    public void ProxyOutcome_Should_Mirror_ServerUsageEventOutcome_MemberForMember()
    {
        // The service serializes UsageEventOutcome as a string (Program.cs registers
        // JsonStringEnumConverter), so the SDK's wire mapping is the member NAME. If the two
        // enums ever drift, feedback silently fails to deserialize server-side.
        var clientNames = Enum.GetNames<ProxyOutcome>();
        var serverNames = Enum.GetNames<UsageEventOutcome>();

        clientNames.ShouldBe(serverNames);
    }
}
