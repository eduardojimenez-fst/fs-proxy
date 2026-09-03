using Mediator;

namespace FSH.Modules.Proxies.Contracts.v1.Tags;

public sealed record DeleteTagCommand(Guid Id) : ICommand;
