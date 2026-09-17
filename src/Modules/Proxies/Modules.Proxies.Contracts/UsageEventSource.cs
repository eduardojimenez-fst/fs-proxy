namespace FSH.Modules.Proxies.Contracts;

/// <summary>Who produced a <c>ProxyUsageEvent</c>: the periodic active probe, or a consumer
/// reporting through the feedback endpoints. Lives in Contracts because it is part of the
/// usage-event read model exposed to operators.</summary>
public enum UsageEventSource { SystemHealthCheck, ConsumerFeedback }
