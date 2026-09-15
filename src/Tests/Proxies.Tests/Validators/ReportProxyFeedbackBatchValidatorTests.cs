using FSH.Modules.Proxies.Contracts;
using FSH.Modules.Proxies.Contracts.Dtos;
using FSH.Modules.Proxies.Contracts.v1.Proxies;
using FSH.Modules.Proxies.Features.v1.Proxies.ReportProxyFeedbackBatch;
using Shouldly;
using Xunit;

namespace Proxies.Tests.Validators;

public sealed class ReportProxyFeedbackBatchValidatorTests
{
    private readonly ReportProxyFeedbackBatchCommandValidator _validator = new();

    private static ProxyFeedbackEvent AnEvent(string? detail = null) =>
        new(Guid.NewGuid(), UsageEventOutcome.Banned, detail);

    [Fact]
    public void Should_Pass_When_BatchIsValid()
    {
        var command = new ReportProxyFeedbackBatchCommand([AnEvent("mercadopublico:robot-check")], null);

        _validator.Validate(command).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Should_Fail_When_BatchIsEmpty()
    {
        var command = new ReportProxyFeedbackBatchCommand([], null);

        _validator.Validate(command).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void Should_Pass_When_BatchIsAtMaxSize()
    {
        var events = Enumerable.Range(0, 200).Select(_ => AnEvent()).ToList();
        var command = new ReportProxyFeedbackBatchCommand(events, null);

        _validator.Validate(command).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Should_Fail_When_BatchExceedsMaxSize()
    {
        var events = Enumerable.Range(0, 201).Select(_ => AnEvent()).ToList();
        var command = new ReportProxyFeedbackBatchCommand(events, null);

        _validator.Validate(command).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void Should_Fail_When_AnyEventHasEmptyProxyId()
    {
        var command = new ReportProxyFeedbackBatchCommand(
            [AnEvent(), new ProxyFeedbackEvent(Guid.Empty, UsageEventOutcome.Failure, null)], null);

        _validator.Validate(command).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void Should_Fail_When_AnyEventHasUnknownOutcome()
    {
        var command = new ReportProxyFeedbackBatchCommand(
            [new ProxyFeedbackEvent(Guid.NewGuid(), (UsageEventOutcome)99, null)], null);

        _validator.Validate(command).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void Should_Fail_When_AnyEventDetailExceeds2048Chars()
    {
        var command = new ReportProxyFeedbackBatchCommand([AnEvent(new string('a', 2049))], null);

        _validator.Validate(command).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void Should_Pass_When_DetailIsAt2048Chars()
    {
        var command = new ReportProxyFeedbackBatchCommand([AnEvent(new string('a', 2048))], null);

        _validator.Validate(command).IsValid.ShouldBeTrue();
    }
}
