using Mediator;

namespace FSH.Modules.Proxies.Contracts.v1.Policies;

public sealed record UnassignPolicyFromTagCommand(Guid TagId) : ICommand;
