using FSH.Framework.Core.Domain;

namespace FSH.Modules.Proxies.Domain;

public sealed class ProxyTagAssignment : IGlobalEntity
{
    public Guid ProxyId { get; private set; }
    public Guid TagId { get; private set; }

    private ProxyTagAssignment() { }

    public static ProxyTagAssignment Create(Guid proxyId, Guid tagId) => new() { ProxyId = proxyId, TagId = tagId };
}
