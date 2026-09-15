using FSH.Proxy.Client;
using FSH.Proxy.Client.Caching;
using Shouldly;
using Xunit;

namespace Proxy.Client.Tests.Caching;

public sealed class FileSnapshotCacheTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "fsproxy-cache-" + Guid.NewGuid().ToString("N"));

    private static IReadOnlyList<ProxyEndpoint> Sample() =>
        [new ProxyEndpoint(Guid.NewGuid(), "203.0.113.10", 8080, ProxyProtocol.Http, "u", "p")];

    [Fact]
    public void Write_Then_Read_Should_Round_Trip()
    {
        var cache = new FileSnapshotCache(_directory, TimeSpan.FromHours(24));
        var written = Sample();

        cache.Write("country:cl", written);
        var read = cache.Read("country:cl");

        read.ShouldNotBeNull();
        read.Count.ShouldBe(1);
        read[0].Id.ShouldBe(written[0].Id);
        read[0].Password.ShouldBe("p");
    }

    [Fact]
    public void Read_Should_Return_Null_For_An_Expired_Entry()
    {
        var cache = new FileSnapshotCache(_directory, TimeSpan.Zero);

        cache.Write("country:cl", Sample());

        cache.Read("country:cl").ShouldBeNull();
    }

    [Fact]
    public void Read_Should_Return_Null_For_A_Missing_Key()
    {
        new FileSnapshotCache(_directory, TimeSpan.FromHours(24)).Read("nope").ShouldBeNull();
    }

    [Fact]
    public void Read_Should_Return_Null_For_A_Corrupt_File_Rather_Than_Throwing()
    {
        // A half-written cache file must degrade to "no cache", never take down a scraper at
        // startup — that is the exact failure this cache exists to prevent.
        var cache = new FileSnapshotCache(_directory, TimeSpan.FromHours(24));
        cache.Write("country:cl", Sample());
        var file = Directory.GetFiles(_directory).Single();
        File.WriteAllText(file, "{ this is not json");

        Should.NotThrow(() => cache.Read("country:cl")).ShouldBeNull();
    }

    [Fact]
    public void Write_Should_Not_Throw_When_The_Directory_Cannot_Be_Created()
    {
        // Caching is best-effort. A read-only disk degrades the cache, it does not fail the scrape.
        var cache = new FileSnapshotCache("/dev/null/definitely-not-a-directory", TimeSpan.FromHours(24));

        Should.NotThrow(() => cache.Write("country:cl", Sample()));
    }

    [Fact]
    public void Different_Tag_Sets_Should_Not_Collide()
    {
        var cache = new FileSnapshotCache(_directory, TimeSpan.FromHours(24));
        var chile = Sample();
        var peru = Sample();

        cache.Write("country:cl", chile);
        cache.Write("country:pe", peru);

        cache.Read("country:cl")![0].Id.ShouldBe(chile[0].Id);
        cache.Read("country:pe")![0].Id.ShouldBe(peru[0].Id);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
