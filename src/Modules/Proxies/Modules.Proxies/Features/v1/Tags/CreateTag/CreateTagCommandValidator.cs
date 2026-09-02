using FluentValidation;
using FSH.Modules.Proxies.Contracts.v1.Tags;

namespace FSH.Modules.Proxies.Features.v1.Tags.CreateTag;

public sealed class CreateTagCommandValidator : AbstractValidator<CreateTagCommand>
{
    public CreateTagCommandValidator() => RuleFor(x => x.Name).NotEmpty().MaximumLength(128);
}
