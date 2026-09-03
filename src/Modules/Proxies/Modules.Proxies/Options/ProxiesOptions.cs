using System.ComponentModel.DataAnnotations;

namespace FSH.Modules.Proxies.Options;

public sealed class ProxiesOptions
{
    [Required, Url]
    public string DefaultHealthCheckTargetUrl { get; set; } = "https://www.google.com/generate_204";

    [Range(500, 30000)]
    public int DefaultHealthCheckTimeoutMs { get; set; } = 5000;

    [Range(1, 1440)]
    public int HealthCheckIntervalMinutes { get; set; } = 15;
}
