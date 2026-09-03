namespace FSH.Modules.Proxies.Contracts.Dtos;

public sealed record ProxyConnectionDto(Guid Id, string Host, int Port, ProxyProtocol Protocol, string? Username, string? Password);
