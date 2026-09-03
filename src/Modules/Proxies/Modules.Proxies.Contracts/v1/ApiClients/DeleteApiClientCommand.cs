using Mediator;

namespace FSH.Modules.Proxies.Contracts.v1.ApiClients;

public sealed record DeleteApiClientCommand(Guid Id) : ICommand;
