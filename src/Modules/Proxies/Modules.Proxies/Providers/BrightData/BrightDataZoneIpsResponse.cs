using System.Text.Json.Serialization;

namespace FSH.Modules.Proxies.Providers.BrightData;

public sealed record BrightDataZoneIpsResponse(
    [property: JsonPropertyName("ips")] IReadOnlyList<BrightDataIpRecord> Ips);

public sealed record BrightDataIpRecord(
    [property: JsonPropertyName("ip")] string Ip,
    [property: JsonPropertyName("port")] int Port,
    [property: JsonPropertyName("customer")] string Customer,
    [property: JsonPropertyName("zone")] string Zone,
    [property: JsonPropertyName("password")] string Password);
