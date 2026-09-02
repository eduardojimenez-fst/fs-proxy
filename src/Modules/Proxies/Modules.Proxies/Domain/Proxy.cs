using FSH.Framework.Core.Domain;
using FSH.Modules.Proxies.Contracts;

namespace FSH.Modules.Proxies.Domain;

public sealed class Proxy : AggregateRoot<Guid>, IGlobalEntity
{
    public Guid ProviderAccountId { get; private set; }
    public string Host { get; private set; } = default!;
    public int Port { get; private set; }
    public ProxyProtocol Protocol { get; private set; }
    public string? Username { get; private set; }
    public string? ProtectedPassword { get; private set; }
    public string? ExternalId { get; private set; }
    public ProxyStatus Status { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime? LastRenewedAtUtc { get; private set; }

    private readonly List<ProxyTagAssignment> _tagAssignments = [];
    public IReadOnlyCollection<ProxyTagAssignment> TagAssignments => _tagAssignments;

    private Proxy() { }

    public static Proxy Create(
        Guid providerAccountId, string host, int port, ProxyProtocol protocol,
        string? username, string? protectedPassword, string? externalId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        return new Proxy
        {
            Id = Guid.CreateVersion7(),
            ProviderAccountId = providerAccountId,
            Host = host.Trim(),
            Port = port,
            Protocol = protocol,
            Username = username,
            ProtectedPassword = protectedPassword,
            ExternalId = externalId,
            Status = ProxyStatus.Testing,
            CreatedAtUtc = DateTime.UtcNow
        };
    }

    public void SetStatus(ProxyStatus status) => Status = status;

    public void UpdateConnection(string host, int port, ProxyProtocol protocol, string? username, string? protectedPassword)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        Host = host.Trim();
        Port = port;
        Protocol = protocol;
        Username = username;
        ProtectedPassword = protectedPassword;
    }

    public void MarkRenewed()
    {
        LastRenewedAtUtc = DateTime.UtcNow;
        Status = ProxyStatus.Testing;
    }

    public void AssignTag(Guid tagId)
    {
        if (_tagAssignments.Any(a => a.TagId == tagId)) return;
        _tagAssignments.Add(ProxyTagAssignment.Create(Id, tagId));
    }

    public void UnassignTag(Guid tagId) => _tagAssignments.RemoveAll(a => a.TagId == tagId);
}
