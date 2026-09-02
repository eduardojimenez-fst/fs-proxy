using FSH.Framework.Core.Exceptions;
using FSH.Modules.Proxies.Contracts.Dtos;
using FSH.Modules.Proxies.Contracts.v1.ProviderAccounts;
using FSH.Modules.Proxies.Data;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Proxies.Features.v1.ProviderAccounts.GetProviderAccountById;

public sealed class GetProviderAccountByIdQueryHandler(ProxiesDbContext dbContext) : IQueryHandler<GetProviderAccountByIdQuery, ProviderAccountDto>
{
    public async ValueTask<ProviderAccountDto> Handle(GetProviderAccountByIdQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        return await dbContext.ProviderAccounts.AsNoTracking()
            .Where(x => x.Id == query.Id)
            .Select(x => new ProviderAccountDto(x.Id, x.Name, x.ProviderType, x.IsEnabled, x.LastSyncedAtUtc, x.LastSyncStatus, x.ConsecutiveSyncFailures))
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException($"Provider account {query.Id} not found.");
    }
}
