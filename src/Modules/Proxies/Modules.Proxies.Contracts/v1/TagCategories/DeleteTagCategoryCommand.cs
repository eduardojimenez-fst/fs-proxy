using Mediator;

namespace FSH.Modules.Proxies.Contracts.v1.TagCategories;

public sealed record DeleteTagCategoryCommand(Guid Id) : ICommand;
