using System.Text.Json.Serialization;

namespace FSH.Modules.Proxies.Providers.Oxylabs;

public sealed record OxylabsProxyListResponse(
    [property: JsonPropertyName("results")] IReadOnlyList<OxylabsProxyRecord> Results);

public sealed record OxylabsProxyRecord(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("ip")] string Ip,
    [property: JsonPropertyName("port")] int Port,
    [property: JsonPropertyName("username")] string Username,
    [property: JsonPropertyName("password")] string Password,
    [property: JsonPropertyName("status")] string Status);
