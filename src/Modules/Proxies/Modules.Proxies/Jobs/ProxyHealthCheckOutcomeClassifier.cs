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
}
