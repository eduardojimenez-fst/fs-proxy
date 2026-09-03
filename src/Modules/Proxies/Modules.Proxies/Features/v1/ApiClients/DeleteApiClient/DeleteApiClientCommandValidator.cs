using FluentValidation;
using FSH.Modules.Proxies.Contracts.v1.ApiClients;

namespace FSH.Modules.Proxies.Features.v1.ApiClients.DeleteApiClient;

public sealed class DeleteApiClientCommandValidator : AbstractValidator<DeleteApiClientCommand>
{
    public DeleteApiClientCommandValidator() => RuleFor(x => x.Id).NotEmpty();
}
