using FSH.Modules.Proxies.Contracts.Dtos;
using FSH.Modules.Proxies.Contracts.v1.TagCategories;
using FSH.Modules.Proxies.Data;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.Proxies.Features.v1.TagCategories.ListTagCategories;

public sealed class ListTagCategoriesQueryHandler(ProxiesDbContext dbContext) : IQueryHandler<ListTagCategoriesQuery, IReadOnlyList<TagCategoryDto>>
{
    public async ValueTask<IReadOnlyList<TagCategoryDto>> Handle(ListTagCategoriesQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return await dbContext.TagCategories.AsNoTracking().Include(x => x.Values).OrderBy(x => x.Name)
            .Select(x => new TagCategoryDto(x.Id, x.Name, x.Values.OrderBy(v => v.Value).Select(v => v.Value).ToList()))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }
}
