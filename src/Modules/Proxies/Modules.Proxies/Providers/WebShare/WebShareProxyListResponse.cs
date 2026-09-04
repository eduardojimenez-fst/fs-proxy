using System.Text.Json.Serialization;

namespace FSH.Modules.Proxies.Providers.WebShare;

public sealed record WebShareProxyListResponse(
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("next")] string? Next,
    [property: JsonPropertyName("results")] IReadOnlyList<WebShareProxyRecord> Results);

public sealed record WebShareProxyRecord(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("username")] string Username,
    [property: JsonPropertyName("password")] string Password,
    [property: JsonPropertyName("proxy_address")] string ProxyAddress,
    [property: JsonPropertyName("port")] int Port,
    [property: JsonPropertyName("valid")] bool Valid,
    [property: JsonPropertyName("country_code")] string? CountryCode = null);
