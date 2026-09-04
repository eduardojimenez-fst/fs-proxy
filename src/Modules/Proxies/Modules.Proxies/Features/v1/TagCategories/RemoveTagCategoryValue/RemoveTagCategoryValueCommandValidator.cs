using FluentValidation;
using FSH.Modules.Proxies.Contracts.v1.TagCategories;

namespace FSH.Modules.Proxies.Features.v1.TagCategories.RemoveTagCategoryValue;

public sealed class RemoveTagCategoryValueCommandValidator : AbstractValidator<RemoveTagCategoryValueCommand>
{
    public RemoveTagCategoryValueCommandValidator()
    {
        RuleFor(x => x.TagCategoryId).NotEmpty();
        RuleFor(x => x.Value).NotEmpty();
    }
}
