using FSH.Modules.Proxies.Data;
using Shouldly;
using Xunit;

namespace Proxies.Tests.Data;

public sealed class TagCategorySeedDataTests
{
    [Fact]
    public void Attachments_Should_Belong_To_OperationType_Only()
    {
        var owningCategories = TagCategorySeedData.Categories
            .Where(category => category.Values.Contains("Attachments", StringComparer.OrdinalIgnoreCase))
            .Select(category => category.Name)
            .ToList();

        // An attachment run is the combination entitytype:tender + operationtype:attachments.
        // Keeping the value under entityType as well makes two different tags look interchangeable,
        // and a pool tagged with the wrong one silently resolves to zero proxies.
        owningCategories.ShouldBe(["operationType"]);
    }

    [Fact]
    public void EntityType_Should_Spell_QuoteAgreementHardwareStorage_Correctly()
    {
        var entityType = TagCategorySeedData.Categories.Single(category => category.Name == "entityType");

        entityType.Values.ShouldContain("QuoteAgreementHardwareStorage");
        entityType.Values.ShouldNotContain("QuoteAgreementHardwareStorare");
    }

    [Fact]
    public void Every_Category_Should_Have_Distinct_Values()
    {
        foreach (var (name, values) in TagCategorySeedData.Categories)
        {
            values.Distinct(StringComparer.OrdinalIgnoreCase).Count()
                .ShouldBe(values.Count, $"category '{name}' declares a duplicate value");
        }
    }
}
