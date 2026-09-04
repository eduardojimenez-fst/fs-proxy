using System.Text.Json.Serialization;

namespace FSH.Modules.Proxies.Providers.BrightData;

public sealed record BrightDataZoneConfigResponse(
    [property: JsonPropertyName("password")] IReadOnlyList<string> Password,
    [property: JsonPropertyName("plan")] BrightDataZonePlan Plan);

public sealed record BrightDataZonePlan(
    [property: JsonPropertyName("country")] string? Country,
    [property: JsonPropertyName("default_country")] string? DefaultCountry);
