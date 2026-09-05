using FSH.Framework.Shared.Persistence;
using FSH.Modules.Proxies.Contracts.Dtos;
using Mediator;

namespace FSH.Modules.Proxies.Contracts.v1.Proxies;

public sealed record ListProxiesQuery(
    IReadOnlyList<string>? Tags, ProxyStatus? Status, Guid? ProviderAccountId,
    string? Geolocation = null, ProxyKind? Kind = null, int PageNumber = 1, int PageSize = 20) : IQuery<PagedResponse<ProxyDto>>;
