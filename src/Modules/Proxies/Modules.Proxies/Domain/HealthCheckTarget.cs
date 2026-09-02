using FSH.Framework.Core.Domain;

namespace FSH.Modules.Proxies.Domain;

public sealed class HealthCheckTarget : AggregateRoot<Guid>, IGlobalEntity
{
    public string Name { get; private set; } = default!;
    public string TestUrl { get; private set; } = default!;
    public int? ExpectedStatusCode { get; private set; }
    public string? ExpectedBodyKeyword { get; private set; }
    public int TimeoutMs { get; private set; }

    private HealthCheckTarget() { }

    public static HealthCheckTarget Create(
        string name, string testUrl, int? expectedStatusCode, string? expectedBodyKeyword, int timeoutMs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(testUrl);
        return new HealthCheckTarget
        {
            Id = Guid.CreateVersion7(),
            Name = name.Trim(),
            TestUrl = testUrl.Trim(),
            ExpectedStatusCode = expectedStatusCode,
            ExpectedBodyKeyword = expectedBodyKeyword,
            TimeoutMs = timeoutMs
        };
    }

    public void Update(string name, string testUrl, int? expectedStatusCode, string? expectedBodyKeyword, int timeoutMs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(testUrl);
        Name = name.Trim();
        TestUrl = testUrl.Trim();
        ExpectedStatusCode = expectedStatusCode;
        ExpectedBodyKeyword = expectedBodyKeyword;
        TimeoutMs = timeoutMs;
    }
}
