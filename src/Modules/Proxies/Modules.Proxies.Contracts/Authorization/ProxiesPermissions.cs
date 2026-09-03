using FSH.Framework.Shared.Constants;

namespace FSH.Modules.Proxies.Contracts.Authorization;

public static class ProxiesPermissions
{
    public static class ProviderAccounts
    {
        public const string Resource = "Proxies.ProviderAccounts";
        public const string View = $"Permissions.{Resource}.View";
        public const string Create = $"Permissions.{Resource}.Create";
        public const string Update = $"Permissions.{Resource}.Update";
        public const string Delete = $"Permissions.{Resource}.Delete";
    }

    public static class ManualProxies
    {
        public const string Resource = "Proxies.ManualProxies";
        public const string View = $"Permissions.{Resource}.View";
        public const string Create = $"Permissions.{Resource}.Create";
        public const string Update = $"Permissions.{Resource}.Update";
        public const string Delete = $"Permissions.{Resource}.Delete";
    }

    public static class Tags
    {
        public const string Resource = "Proxies.Tags";
        public const string View = $"Permissions.{Resource}.View";
        public const string Create = $"Permissions.{Resource}.Create";
        public const string Update = $"Permissions.{Resource}.Update";
        public const string Delete = $"Permissions.{Resource}.Delete";
    }

    public static class Policies
    {
        public const string Resource = "Proxies.Policies";
        public const string View = $"Permissions.{Resource}.View";
        public const string Create = $"Permissions.{Resource}.Create";
        public const string Update = $"Permissions.{Resource}.Update";
        public const string Delete = $"Permissions.{Resource}.Delete";
    }

    public static class HealthCheckTargets
    {
        public const string Resource = "Proxies.HealthCheckTargets";
        public const string View = $"Permissions.{Resource}.View";
        public const string Create = $"Permissions.{Resource}.Create";
        public const string Update = $"Permissions.{Resource}.Update";
        public const string Delete = $"Permissions.{Resource}.Delete";
    }

    public static class ApiClients
    {
        public const string Resource = "Proxies.ApiClients";
        public const string View = $"Permissions.{Resource}.View";
        public const string Create = $"Permissions.{Resource}.Create";
        public const string Delete = $"Permissions.{Resource}.Delete";
    }

    /// <summary>
    /// Gates the consumer-facing endpoints (<c>POST /proxies/request</c> and
    /// <c>POST /proxies/{id}/feedback</c>) for callers authenticating with a JWT rather than an
    /// admin-issued API key. Deliberately NOT <c>IsBasic</c>: <c>request</c> hands back decrypted
    /// proxy passwords and <c>feedback</c> feeds the auto-disable policy engine, so it must be an
    /// explicit grant — this app allows anonymous self-registration, and a merely-authenticated
    /// tenant user must not reach either endpoint.
    /// </summary>
    public static class Consumers
    {
        public const string Resource = "Proxies.Consumers";
        public const string Request = $"Permissions.{Resource}.Request";
    }

    public static IReadOnlyList<FshPermission> All { get; } =
    [
        new("View Provider Accounts", ActionConstants.View, ProviderAccounts.Resource, IsBasic: true),
        new("Create Provider Accounts", ActionConstants.Create, ProviderAccounts.Resource),
        new("Update Provider Accounts", ActionConstants.Update, ProviderAccounts.Resource),
        new("Delete Provider Accounts", ActionConstants.Delete, ProviderAccounts.Resource),

        new("View Manual Proxies", ActionConstants.View, ManualProxies.Resource, IsBasic: true),
        new("Create Manual Proxies", ActionConstants.Create, ManualProxies.Resource),
        new("Update Manual Proxies", ActionConstants.Update, ManualProxies.Resource),
        new("Delete Manual Proxies", ActionConstants.Delete, ManualProxies.Resource),

        new("View Tags", ActionConstants.View, Tags.Resource, IsBasic: true),
        new("Create Tags", ActionConstants.Create, Tags.Resource),
        new("Update Tags", ActionConstants.Update, Tags.Resource),
        new("Delete Tags", ActionConstants.Delete, Tags.Resource),

        new("View Policies", ActionConstants.View, Policies.Resource, IsBasic: true),
        new("Create Policies", ActionConstants.Create, Policies.Resource),
        new("Update Policies", ActionConstants.Update, Policies.Resource),
        new("Delete Policies", ActionConstants.Delete, Policies.Resource),

        new("View Health Check Targets", ActionConstants.View, HealthCheckTargets.Resource, IsBasic: true),
        new("Create Health Check Targets", ActionConstants.Create, HealthCheckTargets.Resource),
        new("Update Health Check Targets", ActionConstants.Update, HealthCheckTargets.Resource),
        new("Delete Health Check Targets", ActionConstants.Delete, HealthCheckTargets.Resource),

        new("View Api Clients", ActionConstants.View, ApiClients.Resource, IsBasic: true),
        new("Create Api Clients", ActionConstants.Create, ApiClients.Resource),
        new("Delete Api Clients", ActionConstants.Delete, ApiClients.Resource),

        new("Request Proxies", "Request", Consumers.Resource, IsBasic: false),
    ];
}
