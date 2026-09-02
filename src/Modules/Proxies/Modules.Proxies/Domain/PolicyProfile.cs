using FSH.Framework.Core.Domain;

namespace FSH.Modules.Proxies.Domain;

public enum PolicyProfileType { Manual, AutoDisable, AutoDisableAndRenew }

public sealed class PolicyProfile : AggregateRoot<Guid>, IGlobalEntity
{
    public string Name { get; private set; } = default!;
    public PolicyProfileType Type { get; private set; }
    public int FailureThreshold { get; private set; }
    public int WindowMinutes { get; private set; }
    public int MinDistinctReporters { get; private set; }

    private PolicyProfile() { }

    public static PolicyProfile Create(
        string name, PolicyProfileType type, int failureThreshold, int windowMinutes, int minDistinctReporters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new PolicyProfile
        {
            Id = Guid.CreateVersion7(),
            Name = name.Trim(),
            Type = type,
            FailureThreshold = failureThreshold,
            WindowMinutes = windowMinutes,
            MinDistinctReporters = minDistinctReporters
        };
    }

    public void Update(string name, PolicyProfileType type, int failureThreshold, int windowMinutes, int minDistinctReporters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name.Trim();
        Type = type;
        FailureThreshold = failureThreshold;
        WindowMinutes = windowMinutes;
        MinDistinctReporters = minDistinctReporters;
    }

    /// <summary>Higher rank wins when a proxy's tags resolve to more than one profile (spec conflict rule).</summary>
    public int RestrictivenessRank => Type switch
    {
        PolicyProfileType.AutoDisableAndRenew => 2,
        PolicyProfileType.AutoDisable => 1,
        _ => 0
    };
}
