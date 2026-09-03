using Mediator;

namespace FSH.Modules.Proxies.Contracts.v1.Tags;

public sealed record CreateTagCommand(string Name) : ICommand<Guid>;
