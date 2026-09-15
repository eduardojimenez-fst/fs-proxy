using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FSH.Proxy.Client.Caching;

/// <summary>
/// A single-directory, one-file-per-key <see cref="IProxySnapshotCache"/>. Each <c>Read</c>/
/// <c>Write</c> key (a tag set, e.g. <c>"country:cl"</c>) is hashed to a filesystem-safe file name —
/// keys carry <c>:</c> and spaces, which are not portable file-name characters, and two different tag
/// sets must never land on the same file.
/// </summary>
/// <remarks>
/// <para>
/// <b>Credentials are stored in plaintext, by explicit decision.</b> This is an internal cache for
/// internal systems, and the status quo it replaces — a hardcoded proxy list — had provider
/// credentials committed directly to source control. A plaintext file in a runtime directory,
/// readable only by the account the scraper runs as, is strictly better than that. The trade-off was
/// made deliberately, not overlooked: do not "fix" it by quietly adding encryption. What IS required
/// of any caller: the cache directory must be a runtime/data directory, never colocated with
/// configuration, and it (or its contents) must be listed in <c>.gitignore</c> so a snapshot never
/// ends up back in source control.
/// </para>
/// <para>
/// <b>Neither <see cref="Read"/> nor <see cref="Write"/> can throw.</b> Every failure mode — a
/// missing or unwritable directory, a corrupt or partially-written file, denied permissions, a path
/// segment that turns out to be a file instead of a directory — is caught and translated into "no
/// cache" (a <see langword="null"/> read, a no-op write). This cache exists purely to widen a
/// scraper's availability when the proxy service is unreachable at startup; a cache that can itself
/// fail the process would defeat that purpose entirely.
/// </para>
/// </remarks>
public sealed class FileSnapshotCache : IProxySnapshotCache
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _directory;
    private readonly TimeSpan _ttl;

    public FileSnapshotCache(string directory, TimeSpan ttl)
    {
#if NET
        ArgumentNullException.ThrowIfNull(directory);
#else
#pragma warning disable CA1510
        if (directory is null) throw new ArgumentNullException(nameof(directory));
#pragma warning restore CA1510
#endif

        _directory = directory;
        _ttl = ttl;
    }

    /// <inheritdoc />
    public IReadOnlyList<ProxyEndpoint>? Read(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return null;
        }

#pragma warning disable CA1031 // Read is a best-effort fallback: every failure (missing/corrupt
        // file, denied permissions, a tampered value that fails ProxyEndpoint's own validation, …)
        // must degrade to "no cache" rather than propagate — see the type's remarks.
        try
        {
            string path = PathFor(key);
            if (!File.Exists(path))
            {
                return null;
            }

            string json = File.ReadAllText(path);
            var document = JsonSerializer.Deserialize<SnapshotDocument>(json, SerializerOptions);
            if (document?.Endpoints is null)
            {
                return null;
            }

            if (DateTime.UtcNow - document.FetchedAtUtc > _ttl)
            {
                return null;
            }

            return document.Endpoints
                .Select(e => new ProxyEndpoint(e.Id, e.Host, e.Port, e.Protocol, e.Username, e.Password))
                .ToList();
        }
        catch (Exception)
        {
            return null;
        }
#pragma warning restore CA1031
    }

    /// <inheritdoc />
    public void Write(string key, IReadOnlyList<ProxyEndpoint> endpoints)
    {
        if (string.IsNullOrEmpty(key) || endpoints is null)
        {
            return;
        }

#pragma warning disable CA1031 // Write is best-effort: every failure (a directory that cannot be
        // created, a read-only filesystem, denied permissions, …) must be swallowed silently — see
        // the type's remarks. Any prior snapshot is left untouched by construction, since the new
        // one is written to a temp file and only moved into place once fully flushed.
        try
        {
            Directory.CreateDirectory(_directory);

            var document = new SnapshotDocument(
                DateTime.UtcNow,
                endpoints.Select(e => new SnapshotEndpoint(e.Id, e.Host, e.Port, e.Protocol, e.Username, e.Password)).ToList());

            string json = JsonSerializer.Serialize(document, SerializerOptions);
            string path = PathFor(key);
            string tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";

            try
            {
                File.WriteAllText(tempPath, json);

                // Write via a temp file then move: a crash or failure mid-write leaves the
                // previous snapshot (if any) exactly as it was, rather than a truncated file.
#if NET
                File.Move(tempPath, path, overwrite: true);
#else
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
                File.Move(tempPath, path);
#endif
            }
            finally
            {
                // Best-effort cleanup: if the move above did not happen (an exception was thrown
                // before or during it), do not leave the temp file behind. File.Exists never
                // throws, so this cannot itself introduce a new failure mode.
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
        }
        catch (Exception)
        {
            // Intentionally swallowed: a cache write must never fail the caller.
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// Maps a cache key to a file path via a SHA-256 hash of its UTF-8 bytes, hex-encoded. Hashing
    /// (rather than sanitizing) sidesteps both problems at once: no key character needs escaping,
    /// and no two distinct keys can ever collide on the same file.
    /// </summary>
    private string PathFor(string key)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(key);
#if NET
        // netstandard2.0 has no static SHA256.HashData; only net10 gets the allocation-free path.
        byte[] hash = SHA256.HashData(bytes);
#else
        using SHA256 sha256 = SHA256.Create();
        byte[] hash = sha256.ComputeHash(bytes);
#endif
        return Path.Combine(_directory, ToHex(hash) + ".json");
    }

    private static string ToHex(byte[] bytes)
    {
        const string HexChars = "0123456789abcdef";
        var chars = new char[bytes.Length * 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            chars[i * 2] = HexChars[bytes[i] >> 4];
            chars[(i * 2) + 1] = HexChars[bytes[i] & 0xF];
        }
        return new string(chars);
    }

    /// <summary>
    /// On-disk document shape: the endpoints plus the UTC instant they were written, which
    /// <see cref="Read"/> compares against <see cref="_ttl"/>.
    /// </summary>
    private sealed class SnapshotDocument
    {
        public SnapshotDocument(DateTime fetchedAtUtc, List<SnapshotEndpoint> endpoints)
        {
            FetchedAtUtc = fetchedAtUtc;
            Endpoints = endpoints;
        }

        public DateTime FetchedAtUtc { get; }

        public List<SnapshotEndpoint> Endpoints { get; }
    }

    /// <summary>
    /// On-disk shape of one endpoint. A separate type from <see cref="ProxyEndpoint"/> on purpose:
    /// <see cref="ProxyEndpoint"/> has no parameterless/default-usable shape for
    /// <see cref="JsonSerializer"/> to target, and coupling the disk format to that public type would
    /// make every future change to it a silent cache-format change too.
    /// </summary>
    private sealed class SnapshotEndpoint
    {
        public SnapshotEndpoint(Guid id, string host, int port, ProxyProtocol protocol, string? username, string? password)
        {
            Id = id;
            Host = host;
            Port = port;
            Protocol = protocol;
            Username = username;
            Password = password;
        }

        public Guid Id { get; }

        public string Host { get; }

        public int Port { get; }

        public ProxyProtocol Protocol { get; }

        public string? Username { get; }

        public string? Password { get; }
    }
}
