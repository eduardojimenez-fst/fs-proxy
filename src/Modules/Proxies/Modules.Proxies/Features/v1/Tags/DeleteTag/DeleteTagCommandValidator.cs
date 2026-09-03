using FluentValidation;
using FSH.Modules.Proxies.Contracts.v1.Tags;

namespace FSH.Modules.Proxies.Features.v1.Tags.DeleteTag;

public sealed class DeleteTagCommandValidator : AbstractValidator<DeleteTagCommand>
{
    public DeleteTagCommandValidator() => RuleFor(x => x.Id).NotEmpty();
}
