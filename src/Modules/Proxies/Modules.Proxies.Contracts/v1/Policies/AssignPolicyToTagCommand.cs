using Mediator;

namespace FSH.Modules.Proxies.Contracts.v1.Policies;

public sealed record AssignPolicyToTagCommand(Guid TagId, Guid PolicyProfileId) : ICommand;
