using FSH.Modules.Proxies.Contracts.Dtos;
using Mediator;

namespace FSH.Modules.Proxies.Contracts.v1.ApiClients;

public sealed record ListApiClientsQuery : IQuery<IReadOnlyList<ApiClientDto>>;
