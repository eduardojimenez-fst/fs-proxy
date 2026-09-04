using FluentValidation;
using FSH.Modules.Proxies.Contracts.v1.TagCategories;

namespace FSH.Modules.Proxies.Features.v1.TagCategories.DeleteTagCategory;

public sealed class DeleteTagCategoryCommandValidator : AbstractValidator<DeleteTagCategoryCommand>
{
    public DeleteTagCategoryCommandValidator() => RuleFor(x => x.Id).NotEmpty();
}
