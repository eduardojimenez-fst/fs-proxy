using Mediator;

namespace FSH.Modules.Proxies.Contracts.v1.TagCategories;

public sealed record AddTagCategoryValueCommand(Guid TagCategoryId, string Value) : ICommand;
