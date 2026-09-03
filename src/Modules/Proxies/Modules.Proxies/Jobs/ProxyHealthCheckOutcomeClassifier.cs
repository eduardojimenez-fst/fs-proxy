using System.Net;
using FSH.Modules.Proxies.Contracts;

namespace FSH.Modules.Proxies.Jobs;

public static class ProxyHealthCheckOutcomeClassifier
{
    public static UsageEventOutcome Classify(bool timedOut, HttpStatusCode? statusCode, string? body, int? expectedStatusCode, string? expectedBodyKeyword)
    {
        if (timedOut || statusCode is null) return UsageEventOutcome.Timeout;

        bool statusOk = expectedStatusCode is { } expected ? (int)statusCode == expected : (int)statusCode is >= 200 and < 400;
        if (!statusOk) return UsageEventOutcome.Failure;

        if (!string.IsNullOrEmpty(expectedBodyKeyword) &&
            !(body?.Contains(expectedBodyKeyword, StringComparison.OrdinalIgnoreCase) ?? false))
        {
            return UsageEventOutcome.Failure;
        }

        return UsageEventOutcome.Success;
    }

    /// <summary>
    /// A proxy sits in <see cref="ProxyStatus.Testing"/> from the moment it is synced
    /// (<c>Proxy.Create</c>) or renewed (<c>Proxy.MarkRenewed</c>). The active health check is the
    /// only thing that promotes it: one successful probe is enough to make it servable. Anything
    /// other than a successful probe on a Testing proxy leaves the status alone — the recorded
    /// usage event plus <c>PolicyEvaluationService</c> already own that decision.
    /// </summary>
    public static bool ShouldPromoteToActive(ProxyStatus currentStatus, UsageEventOutcome outcome) =>
        currentStatus == ProxyStatus.Testing && outcome == UsageEventOutcome.Success;
}
