using FSH.Modules.Proxies.Contracts.Dtos;
using Mediator;

namespace FSH.Modules.Proxies.Contracts.v1.TagCategories;

public sealed record ListTagCategoriesQuery : IQuery<IReadOnlyList<TagCategoryDto>>;
