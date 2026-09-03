using FluentValidation;
using FSH.Modules.Proxies.Contracts.v1.ApiClients;

namespace FSH.Modules.Proxies.Features.v1.ApiClients.CreateApiClient;

public sealed class CreateApiClientCommandValidator : AbstractValidator<CreateApiClientCommand>
{
    public CreateApiClientCommandValidator() => RuleFor(x => x.Name).NotEmpty().MaximumLength(128);
}
