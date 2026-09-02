namespace FSH.Modules.Proxies.Contracts;

/// <summary>
/// Anchor type used by Mediator assembly scanning to register this project's future
/// commands, queries, and handlers (added starting Task 2+).
/// </summary>
public static class ProxiesContractsMarker
{
    // TEMPORARY — DELETE once this project has a real command/query (Task 2+).
    // Forces a genuine metadata reference to Mediator.Abstractions on this assembly's public
    // API *signature* — the reference must appear in a member signature (not just a method
    // body/initializer), because reference-assembly generation strips method bodies (including
    // static field initializers) but keeps signatures. Without this, the Mediator source
    // generator's MSG0007 check can't recognize this assembly as a scannable target, because
    // right now this project has no other type that references Mediator on its own. Once a
    // real ICommand/IQuery lands here, it will supply that reference naturally and this
    // member becomes dead weight — remove it then.
    public static Mediator.INotification? AnchorNotification => null;
}
