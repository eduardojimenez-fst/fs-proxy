using FSH.Modules.Proxies.Data;
using FSH.Proxy.Client;
using Shouldly;
using Xunit;

namespace Proxy.Client.Tests;

public sealed class ProxyTagsTests
{
    [Fact]
    public void Constants_Should_Already_Be_Normalized()
    {
        // Tag.Normalize on the server is trim + lowercase. A constant that is not already in that
        // form would still match (the SDK normalizes on the way out) but would read as a lie.
        ProxyTags.Country.Chile.ShouldBe("country:cl");
        ProxyTags.EntityType.Tender.ShouldBe("entitytype:tender");
        ProxyTags.OperationType.Attachments.ShouldBe("operationtype:attachments");
    }

    [Fact]
    public void Normalize_Should_Match_The_Server_Rule()
    {
        ProxyTags.Normalize("  EntityType:Tender  ").ShouldBe("entitytype:tender");
    }

    [Fact]
    public void Of_Should_Compose_And_Normalize()
    {
        ProxyTags.Of("EntityType", "Tender").ShouldBe("entitytype:tender");
    }

    [Fact]
    public void Every_Seeded_Catalog_Value_Should_Have_A_Constant()
    {
        // The SDK targets netstandard2.0 and cannot reference Modules.Proxies, so these constants
        // are hand-maintained. This test is the only thing stopping them drifting from the seed.
        var expected = TagCategorySeedData.Categories
            .SelectMany(category => category.Values.Select(value => ProxyTags.Of(category.Name, value)))
            .OrderBy(tag => tag, StringComparer.Ordinal)
            .ToList();

        var actual = ProxyTags.All.OrderBy(tag => tag, StringComparer.Ordinal).ToList();

        actual.ShouldBe(expected);
    }
}
