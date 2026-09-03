using FSH.Modules.Proxies.Contracts.Dtos;
using FSH.Modules.Proxies.Contracts.v1.ApiClients;
using FSH.Modules.Proxies.Data;
using FSH.Modules.Proxies.Domain;
using FSH.Modules.Proxies.Services;
using Mediator;

namespace FSH.Modules.Proxies.Features.v1.ApiClients.CreateApiClient;

public sealed class CreateApiClientCommandHandler(ProxiesDbContext dbContext, IApiKeyHasher hasher)
    : ICommandHandler<CreateApiClientCommand, CreateApiClientResult>
{
    public async ValueTask<CreateApiClientResult> Handle(CreateApiClientCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var (plaintextKey, hash) = hasher.GenerateKey();
        var client = ApiClient.Create(command.Name, hash);
        dbContext.ApiClients.Add(client);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new CreateApiClientResult(client.Id, plaintextKey);
    }
}
