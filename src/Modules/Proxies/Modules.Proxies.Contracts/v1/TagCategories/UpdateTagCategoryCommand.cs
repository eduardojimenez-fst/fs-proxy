using Mediator;

namespace FSH.Modules.Proxies.Contracts.v1.TagCategories;

public sealed record UpdateTagCategoryCommand(Guid Id, string Name) : ICommand;
