namespace FSH.Modules.Proxies.Contracts;

/// <summary>
/// Anchor type used by Mediator assembly scanning to register this project's future
/// commands, queries, and handlers (added starting Task 2+).
/// </summary>
public static class ProxiesContractsMarker
{
    // Forces a genuine metadata reference to Mediator.Abstractions on this assembly's public
    // API *signature* — the reference must appear in a member signature (not just a method
    // body/initializer), because reference-assembly generation strips method bodies (including
    // static field initializers) but keeps signatures. Without this, the Mediator source
    // generator can't recognize this assembly as a scannable target before any real
    // command/query type exists here yet.
    public static Mediator.INotification? AnchorNotification => null;
}
