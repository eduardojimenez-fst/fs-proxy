using FSH.Framework.Shared.Persistence;
using FSH.Modules.Proxies.Contracts.Dtos;
using Mediator;

namespace FSH.Modules.Proxies.Contracts.v1.Proxies;

/// <summary><see cref="Host"/> and <see cref="Username"/> are case-insensitive "contains" searches.</summary>
public sealed record ListProxiesQuery(
    IReadOnlyList<string>? Tags, ProxyStatus? Status, Guid? ProviderAccountId,
    string? Geolocation = null, ProxyKind? Kind = null, string? Host = null, string? Username = null,
    int PageNumber = 1, int PageSize = 20) : IQuery<PagedResponse<ProxyDto>>;
