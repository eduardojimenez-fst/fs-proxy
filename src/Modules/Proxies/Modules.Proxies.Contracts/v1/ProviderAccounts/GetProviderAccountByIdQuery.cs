using FSH.Modules.Proxies.Contracts.Dtos;
using Mediator;

namespace FSH.Modules.Proxies.Contracts.v1.ProviderAccounts;

public sealed record GetProviderAccountByIdQuery(Guid Id) : IQuery<ProviderAccountDto>;
