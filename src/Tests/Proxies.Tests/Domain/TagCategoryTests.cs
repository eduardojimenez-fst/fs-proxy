using FSH.Modules.Proxies.Domain;
using Shouldly;
using Xunit;

namespace Proxies.Tests.Domain;

public sealed class TagCategoryTests
{
    [Fact]
    public void Create_Should_NormalizeName()
    {
        var category = TagCategory.Create("  Pais  ");

        category.Name.ShouldBe("Pais");
        category.Values.ShouldBeEmpty();
    }

    [Fact]
    public void Rename_Should_NormalizeNewName()
    {
        var category = TagCategory.Create("pais");

        category.Rename("  Country  ");

        category.Name.ShouldBe("Country");
    }

    [Fact]
    public void AddValue_Should_NormalizeAndAppend()
    {
        var category = TagCategory.Create("pais");

        category.AddValue("  CL  ");

        category.Values.Single().Value.ShouldBe("CL");
        category.Values.Single().TagCategoryId.ShouldBe(category.Id);
    }

    [Fact]
    public void AddValue_Should_Throw_When_ValueAlreadyExists_CaseInsensitive()
    {
        var category = TagCategory.Create("pais");
        category.AddValue("cl");

        Should.Throw<InvalidOperationException>(() => category.AddValue("CL"));
    }

    [Fact]
    public void RemoveValue_Should_RemoveMatchingValue_CaseInsensitive()
    {
        var category = TagCategory.Create("pais");
        category.AddValue("cl");
        category.AddValue("ar");

        category.RemoveValue("CL");

        category.Values.Select(v => v.Value).ShouldBe(["ar"]);
    }
}
