using System.Text.Json.Serialization;

namespace FSH.Modules.Proxies.Providers.BrightData;

public sealed record BrightDataZoneIpsResponse(
    [property: JsonPropertyName("ips")] IReadOnlyList<BrightDataZoneIpRecord> Ips);

public sealed record BrightDataZoneIpRecord(
    [property: JsonPropertyName("ip")] string Ip,
    [property: JsonPropertyName("maxmind")] string? Maxmind);
