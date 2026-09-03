using FSH.Framework.Shared.Persistence;
using FSH.Modules.Proxies.Contracts.Dtos;
using Mediator;

namespace FSH.Modules.Proxies.Contracts.v1.ProviderAccounts;

public sealed record ListProviderAccountsQuery(int PageNumber = 1, int PageSize = 20) : IQuery<PagedResponse<ProviderAccountDto>>;
