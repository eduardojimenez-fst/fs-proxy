using FluentValidation;
using FSH.Modules.Proxies.Contracts.v1.TagCategories;

namespace FSH.Modules.Proxies.Features.v1.TagCategories.UpdateTagCategory;

public sealed class UpdateTagCategoryCommandValidator : AbstractValidator<UpdateTagCategoryCommand>
{
    public UpdateTagCategoryCommandValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Name).NotEmpty().MaximumLength(128);
    }
}
