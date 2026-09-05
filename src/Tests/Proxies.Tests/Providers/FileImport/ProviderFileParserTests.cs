using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Providers.FileImport;
using Shouldly;
using Xunit;

namespace Proxies.Tests.Providers.FileImport;

public sealed class ProviderFileParserTests
{
    private const string Header = "Host,Port,Protocol,Username,Password,Geolocation,ProxyKind";

    [Fact]
    public void Parse_Should_ParseFullyPopulatedRow()
    {
        var csv = $"{Header}\n89.249.195.245,7000,Http,jgwcycpg,ytz1gdtc8ymc,CL,Residential";

        var result = ProviderFileParser.Parse(csv);

        result.Errors.ShouldBeEmpty();
        var record = result.Records.ShouldHaveSingleItem();
        record.ExternalId.ShouldBe("file:89.249.195.245:7000");
        record.Host.ShouldBe("89.249.195.245");
        record.Port.ShouldBe(7000);
        record.Protocol.ShouldBe(ProxyProtocol.Http);
        record.Username.ShouldBe("jgwcycpg");
        record.Password.ShouldBe("ytz1gdtc8ymc");
        record.Geolocation.ShouldBe("CL");
        record.Kind.ShouldBe(ProxyKind.Residential);
        record.IsActive.ShouldBeTrue();
    }

    [Fact]
    public void Parse_Should_TreatBlankOptionalColumns_AsNull_And_DefaultProtocolToHttp()
    {
        var csv = $"{Header}\ndc.oxylabs.io,8007,,,,CL,DataCenter";

        var result = ProviderFileParser.Parse(csv);

        var record = result.Records.ShouldHaveSingleItem();
        record.Protocol.ShouldBe(ProxyProtocol.Http);
        record.Username.ShouldBeNull();
        record.Password.ShouldBeNull();
    }

    [Fact]
    public void Parse_Should_ReportRowError_When_HostIsBlank()
    {
        var csv = $"{Header}\n,8007,Http,u,p,CL,DataCenter";

        var result = ProviderFileParser.Parse(csv);

        result.Records.ShouldBeEmpty();
        var error = result.Errors.ShouldHaveSingleItem();
        error.LineNumber.ShouldBe(2);
        error.Message.ShouldContain("Host");
    }

    [Fact]
    public void Parse_Should_ReportRowError_When_PortIsNotAnInteger()
    {
        var csv = $"{Header}\ndc.oxylabs.io,notaport,Http,u,p,CL,DataCenter";

        var result = ProviderFileParser.Parse(csv);

        result.Records.ShouldBeEmpty();
        result.Errors.ShouldHaveSingleItem().Message.ShouldContain("port");
    }

    [Fact]
    public void Parse_Should_ReportRowError_When_ProtocolIsUnrecognized()
    {
        var csv = $"{Header}\ndc.oxylabs.io,8007,Ftp,u,p,CL,DataCenter";

        var result = ProviderFileParser.Parse(csv);

        result.Records.ShouldBeEmpty();
        result.Errors.ShouldHaveSingleItem().Message.ShouldContain("protocol");
    }

    [Fact]
    public void Parse_Should_ReportRowError_When_ProxyKindIsUnrecognized()
    {
        var csv = $"{Header}\ndc.oxylabs.io,8007,Http,u,p,CL,Satellite";

        var result = ProviderFileParser.Parse(csv);

        result.Records.ShouldBeEmpty();
        result.Errors.ShouldHaveSingleItem().Message.ShouldContain("proxy kind");
    }

    [Fact]
    public void Parse_Should_ContinuePastBadRows_And_KeepValidOnes()
    {
        var csv = $"{Header}\n,8007,Http,u,p,CL,DataCenter\ndc.oxylabs.io,8008,Http,u,p,CL,DataCenter";

        var result = ProviderFileParser.Parse(csv);

        result.Records.ShouldHaveSingleItem().Host.ShouldBe("dc.oxylabs.io");
        result.Errors.ShouldHaveSingleItem().LineNumber.ShouldBe(2);
    }

    [Fact]
    public void Parse_Should_ReportRowError_When_ProtocolIsNumericString()
    {
        var csv = $"{Header}\ndc.oxylabs.io,8007,9,u,p,CL,DataCenter";

        var result = ProviderFileParser.Parse(csv);

        result.Records.ShouldBeEmpty();
        result.Errors.ShouldHaveSingleItem().Message.ShouldContain("protocol");
    }

    [Fact]
    public void Parse_Should_ReportRowError_When_ProxyKindIsNumericString()
    {
        var csv = $"{Header}\ndc.oxylabs.io,8007,Http,u,p,CL,42";

        var result = ProviderFileParser.Parse(csv);

        result.Records.ShouldBeEmpty();
        result.Errors.ShouldHaveSingleItem().Message.ShouldContain("proxy kind");
    }

    [Fact]
    public void Parse_Should_ReportRowError_When_ColumnCountIsTooFew()
    {
        var csv = $"{Header}\ndc.oxylabs.io,8007";

        var result = ProviderFileParser.Parse(csv);

        result.Records.ShouldBeEmpty();
        var error = result.Errors.ShouldHaveSingleItem();
        error.LineNumber.ShouldBe(2);
        error.Message.ShouldContain("column");
    }

    [Fact]
    public void Parse_Should_ReportRowError_When_ExternalIdDuplicatesAnEarlierRowInTheSameFile()
    {
        var csv = $"{Header}\n89.249.195.245,7000,Http,u,p,,\n89.249.195.245,7000,Http,u2,p2,,";

        var result = ProviderFileParser.Parse(csv);

        result.Records.ShouldHaveSingleItem().ExternalId.ShouldBe("file:89.249.195.245:7000");
        var error = result.Errors.ShouldHaveSingleItem();
        error.LineNumber.ShouldBe(3);
        error.Message.ShouldContain("Duplicate");
        error.Message.ShouldContain("line 2");
    }

    [Fact]
    public void Parse_Should_Throw_When_FileIsEmpty() =>
        Should.Throw<FormatException>(() => ProviderFileParser.Parse(""));

    [Fact]
    public void Parse_Should_Throw_When_HeaderDoesNotMatch() =>
        Should.Throw<FormatException>(() => ProviderFileParser.Parse("Wrong,Header\n1.2.3.4,80"));
}
