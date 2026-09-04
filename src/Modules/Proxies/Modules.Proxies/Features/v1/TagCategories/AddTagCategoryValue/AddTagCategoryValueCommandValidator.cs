using FluentValidation;
using FSH.Modules.Proxies.Contracts.v1.TagCategories;

namespace FSH.Modules.Proxies.Features.v1.TagCategories.AddTagCategoryValue;

public sealed class AddTagCategoryValueCommandValidator : AbstractValidator<AddTagCategoryValueCommand>
{
    public AddTagCategoryValueCommandValidator()
    {
        RuleFor(x => x.TagCategoryId).NotEmpty();
        RuleFor(x => x.Value).NotEmpty().MaximumLength(128);
    }
}
