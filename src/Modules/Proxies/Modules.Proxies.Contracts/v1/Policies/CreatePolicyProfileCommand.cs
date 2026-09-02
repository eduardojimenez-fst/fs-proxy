using Mediator;

namespace FSH.Modules.Proxies.Contracts.v1.Policies;

public sealed record CreatePolicyProfileCommand(
    string Name, PolicyProfileType Type, int FailureThreshold, int WindowMinutes, int MinDistinctReporters) : ICommand<Guid>;
