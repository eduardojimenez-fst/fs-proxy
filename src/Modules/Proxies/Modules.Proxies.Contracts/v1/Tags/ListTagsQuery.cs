using FSH.Modules.Proxies.Contracts.Dtos;
using Mediator;

namespace FSH.Modules.Proxies.Contracts.v1.Tags;

public sealed record ListTagsQuery : IQuery<IReadOnlyList<TagDto>>;
