namespace FSH.Modules.Proxies.Contracts.Dtos;

/// <summary>
/// Outcome of a batched feedback submission. <paramref name="Rejected"/> lists the distinct proxy ids
/// that no longer exist — a proxy can be retired between the moment a client leases it and the moment
/// the buffered event is flushed, and failing the whole batch for that would make the client retry a
/// submission that can never succeed.
/// </summary>
public sealed record BatchFeedbackResult(int Accepted, IReadOnlyList<Guid> Rejected);
