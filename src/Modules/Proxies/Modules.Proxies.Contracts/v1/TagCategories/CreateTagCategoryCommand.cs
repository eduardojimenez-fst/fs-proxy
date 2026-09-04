using Mediator;

namespace FSH.Modules.Proxies.Contracts.v1.TagCategories;

public sealed record CreateTagCategoryCommand(string Name) : ICommand<Guid>;
