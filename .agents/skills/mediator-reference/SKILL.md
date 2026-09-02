---
name: mediator-reference
description: CQRS interface reference for FSH. This project uses the Mediator source generator, NOT MediatR. Reference when implementing commands, queries, and handlers.
user-invocable: false
---

# Mediator Reference

⚠️ **FSH uses the `Mediator` source-generator package (`using Mediator;`), NOT `MediatR`.** Different
interfaces — MediatR types won't compile. The CQRS interfaces below are the library's own (no FSH wrapper).

## Interfaces

| Purpose | ✅ Mediator (use) | ❌ MediatR (don't) |
|---|---|---|
| Command | `ICommand<TResponse>` | `IRequest<TResponse>` |
| Query | `IQuery<TResponse>` | `IRequest<TResponse>` |
| Command handler | `ICommandHandler<TCommand, TResponse>` | `IRequestHandler<…>` |
| Query handler | `IQueryHandler<TQuery, TResponse>` | `IRequestHandler<…>` |
| Notification / domain event | `INotification` (`IDomainEvent : INotification`) | `INotification` |

## Pattern

```csharp
// Command/Query → the Contracts project (Modules.{X}.Contracts/v1/{Area}/)
public sealed record Create{Entity}Command(string Name) : ICommand<Guid>;

// Handler → the runtime project (Modules.{X}/Features/v1/{Area}/{Feature}/), public sealed
public sealed class Create{Entity}CommandHandler({X}DbContext db)
    : ICommandHandler<Create{Entity}Command, Guid>
{
    public async ValueTask<Guid> Handle(Create{Entity}Command command, CancellationToken cancellationToken)
    {
        // …
    }
}
```

Rules: handlers return **`ValueTask<T>`** (not `Task<T>`); parameter named `command`/`query` (not `request`);
`public sealed`; `.ConfigureAwait(false)` on awaits. Send via `mediator.Send(command, ct)` (the `IMediator`
interface name matches MediatR's — that part is fine).

## Registration — the four places

The source generator only scans assemblies listed in `o.Assemblies`, and that list exists in **two host
files**. A new module needs **two markers** (a Contracts type **and** the module type) added to the Mediator
list **plus** an entry in the `moduleAssemblies` array — in **both** `FS.Proxy.Api/Program.cs` **and**
`FS.Proxy.DbMigrator/Program.cs`:

```csharp
builder.Services.AddMediator(o =>
{
    o.ServiceLifetime = ServiceLifetime.Scoped;
    o.Assemblies = [
        /* … */
        typeof(FSH.Modules.{X}.Contracts.{X}ContractsMarker),   // Contracts assembly
        typeof(FSH.Modules.{X}.{X}Module)];                     // runtime assembly
});
```

See `add-module` for the full procedure.

## Common errors

| Symptom | Cause → fix |
|---|---|
| `IRequest<T>` / `IRequestHandler<,>` not found | MediatR interface → use `ICommand`/`IQuery` + `ICommandHandler`/`IQueryHandler` |
| `Task<T>` vs `ValueTask<T>` mismatch | Handler must return `ValueTask<T>` |
| Handler not invoked at runtime | Assembly missing from `o.Assemblies` (in one or both Program.cs files) |
