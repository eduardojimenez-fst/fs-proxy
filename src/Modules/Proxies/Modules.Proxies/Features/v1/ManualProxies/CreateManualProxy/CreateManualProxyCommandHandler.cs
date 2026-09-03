using FSH.Modules.Proxies.Contracts.v1.ManualProxies;
using FSH.Modules.Proxies.Data;
using FSH.Modules.Proxies.Domain;
using FSH.Modules.Proxies.Services;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FSH.Modules.Proxies.Features.v1.ManualProxies.CreateManualProxy;

public sealed class CreateManualProxyCommandHandler(
    ProxiesDbContext dbContext, [FromKeyedServices("proxy-password")] IProxySecretProtector proxyPasswordProtector)
    : ICommandHandler<CreateManualProxyCommand, Guid>
{
    public async ValueTask<Guid> Handle(CreateManualProxyCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        string? protectedPassword = string.IsNullOrWhiteSpace(command.PlaintextPassword)
            ? null : proxyPasswordProtector.Protect(command.PlaintextPassword);

        var proxy = Proxy.Create(ManualProviderAccount.Id, command.Host, command.Port, command.Protocol, command.Username, protectedPassword, externalId: null);

        foreach (var tagId in await ResolveTagIdsAsync(dbContext, command.TagNames, cancellationToken).ConfigureAwait(false))
        {
            proxy.AssignTag(tagId);
        }

        dbContext.Proxies.Add(proxy);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return proxy.Id;
    }

    internal static async Task<List<Guid>> ResolveTagIdsAsync(ProxiesDbContext dbContext, IReadOnlyList<string> tagNames, CancellationToken cancellationToken)
    {
        var normalized = tagNames.Select(Tag.Normalize).Distinct().ToList();
        var existing = await dbContext.Tags.Where(t => normalized.Contains(t.Name)).ToListAsync(cancellationToken).ConfigureAwait(false);
        var toCreate = normalized.Except(existing.Select(t => t.Name)).Select(Tag.Create).ToList();
        if (toCreate.Count > 0)
        {
            dbContext.Tags.AddRange(toCreate);
        }
        return [.. existing.Select(t => t.Id), .. toCreate.Select(t => t.Id)];
    }
}
