using FSH.Proxy.Client;
using Shouldly;
using Xunit;

namespace Proxy.Client.Tests;

/// <summary>
/// Promoted ledger minor: <see cref="ProxyClientOptions.Validate"/> used to range-check only five of
/// its eleven knobs. <c>FeedbackQueueCapacity = 0</c> would silently drop every single event
/// <c>Report</c> is ever called with, and <c>RefreshInterval = TimeSpan.Zero</c> would yield a ~1ms
/// refresh loop hammering the service — both previously passed validation. Every TimeSpan/int knob is
/// checked now, not only the five that happened to be checked first.
/// </summary>
public sealed class ProxyClientOptionsTests
{
    private static ProxyClientOptions Valid() => new()
    {
        BaseAddress = new Uri("https://proxy.test"),
        ApiKey = "key",
    };

    [Fact]
    public void Validate_Should_Not_Throw_For_The_Defaults()
    {
        Should.NotThrow(() => Valid().Validate());
    }

    [Fact]
    public void Validate_Should_Reject_A_Zero_FeedbackQueueCapacity()
    {
        var options = Valid();
        options.FeedbackQueueCapacity = 0;

        Should.Throw<InvalidOperationException>(() => options.Validate())
            .Message.ShouldContain(nameof(ProxyClientOptions.FeedbackQueueCapacity));
    }

    [Fact]
    public void Validate_Should_Reject_A_Zero_RefreshInterval()
    {
        var options = Valid();
        options.RefreshInterval = TimeSpan.Zero;

        Should.Throw<InvalidOperationException>(() => options.Validate())
            .Message.ShouldContain(nameof(ProxyClientOptions.RefreshInterval));
    }

    [Fact]
    public void Validate_Should_Reject_A_Negative_RefreshInterval()
    {
        var options = Valid();
        options.RefreshInterval = TimeSpan.FromSeconds(-1);

        Should.Throw<InvalidOperationException>(() => options.Validate());
    }

    [Fact]
    public void Validate_Should_Reject_A_Zero_StaleCeiling()
    {
        var options = Valid();
        options.StaleCeiling = TimeSpan.Zero;

        Should.Throw<InvalidOperationException>(() => options.Validate())
            .Message.ShouldContain(nameof(ProxyClientOptions.StaleCeiling));
    }

    [Fact]
    public void Validate_Should_Reject_A_Negative_Quarantine()
    {
        var options = Valid();
        options.Quarantine = TimeSpan.FromSeconds(-1);

        Should.Throw<InvalidOperationException>(() => options.Validate())
            .Message.ShouldContain(nameof(ProxyClientOptions.Quarantine));
    }

    [Fact]
    public void Validate_Should_Accept_A_Zero_Quarantine()
    {
        // Zero means "never quarantine" — a degenerate but legitimate configuration, unlike a
        // negative value (meaningless) or a zero interval/capacity (breaks the feature entirely).
        var options = Valid();
        options.Quarantine = TimeSpan.Zero;

        Should.NotThrow(() => options.Validate());
    }

    [Fact]
    public void Validate_Should_Reject_A_Zero_FeedbackFlushInterval()
    {
        var options = Valid();
        options.FeedbackFlushInterval = TimeSpan.Zero;

        Should.Throw<InvalidOperationException>(() => options.Validate())
            .Message.ShouldContain(nameof(ProxyClientOptions.FeedbackFlushInterval));
    }

    [Fact]
    public void Validate_Should_Reject_A_Negative_FeedbackQueueCapacity()
    {
        var options = Valid();
        options.FeedbackQueueCapacity = -1;

        Should.Throw<InvalidOperationException>(() => options.Validate());
    }
}
