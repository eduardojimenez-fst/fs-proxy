using FluentValidation;
using FSH.Modules.Proxies.Contracts.v1.TagCategories;

namespace FSH.Modules.Proxies.Features.v1.TagCategories.CreateTagCategory;

public sealed class CreateTagCategoryCommandValidator : AbstractValidator<CreateTagCategoryCommand>
{
    public CreateTagCategoryCommandValidator() => RuleFor(x => x.Name).NotEmpty().MaximumLength(128);
}
