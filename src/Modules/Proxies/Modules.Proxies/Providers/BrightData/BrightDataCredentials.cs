namespace FSH.Modules.Proxies.Providers.BrightData;

public sealed record BrightDataCredentials(
    string ApiToken, string Zone, string CustomerId, int GatewayPort, string GatewayHost = "brd.superproxy.io");
