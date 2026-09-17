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

    /// <summary>
    /// How long ProxyUsageEvent rows are kept before the daily purge job removes them. Must stay
    /// comfortably above the widest PolicyProfile.WindowMinutes in use, or the policy engine will
    /// count against a window whose older events have already been deleted.
    /// </summary>
    [Range(1, 3650)]
    public int UsageEventRetentionDays { get; set; } = 30;
}
