# Proxy Client SDK (Phase 2) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship `FS.Proxy.Client`, a multi-targeted NuGet package that lets .NET Framework 4.8 and .NET 10 scrapers pull proxies from the Proxy Management Service and report outcomes back, without either family restructuring how it makes HTTP calls.

**Architecture:** A background refresher keeps an immutable snapshot of proxies per tag-set in memory; every read path (`GetProxies`, `Lease`) is synchronous and does no I/O, because the consuming scrapers are full of `.Result` and an async-only surface would deadlock them. Outcome classification — deciding whether a timeout, a 403 or a 503 blamed the proxy or the portal — lives here and only here. Feedback is buffered and flushed in batches to the endpoint phase 1 added.

**Tech Stack:** .NET 10 + .NET Standard 2.0 (multi-target), `System.Text.Json`, xUnit + Shouldly + NSubstitute. No other runtime dependency on the `netstandard2.0` target.

**Spec:** `docs/superpowers/specs/2026-09-14-proxy-client-sdk-design.md` — §1 (three levels of adoption), §2 (pool lifecycle, snapshot cache), §3 (outcome classification), §4 client side (feedback buffering), §5 (tag catalog), §7 (configuration), §8 (packaging), §9 (testing).

**Prerequisite, already done:** phase 1 shipped `POST /api/v1/proxies/feedback/batch` and the tag catalog was corrected in source, QA and Production. API keys exist for testing.

## Global Constraints

- **Target frameworks: `netstandard2.0;net10.0`.** `src/Directory.Build.props:4` sets the singular `<TargetFramework>net10.0</TargetFramework>` for everything under `src/`. A project setting `TargetFrameworks` while the singular property is also set builds **only** net10 in silence. The csproj must clear the singular property explicitly.
- `TreatWarningsAsErrors` is on and `AnalysisMode=AllEnabledByDefault`, on **both** targets. `netstandard2.0` lacks nullable attributes, `Index`/`Range`, and `ArgumentNullException.ThrowIfNull`.
- **Central package management is on** (`ManagePackageVersionsCentrally`): `PackageReference` carries no `Version`; every version lives in `src/Directory.Packages.props`.
- **No dependency outside the BCL on the `netstandard2.0` target** beyond `System.Text.Json`. DI and `IConfiguration` binding are net10-only conveniences behind `#if NET`.
- **Every read path a scraper calls must be synchronous and free of I/O.** `WarmupAsync` is the one exception and runs once per process.
- **Credentials never touch a log.** `ProxyEndpoint.ToString()` masks the password; nothing else prints it.
- File-scoped namespaces · 4-space indent · explicit types (`var` only when the right-hand side is obvious) · `is null` / `is not null` · records for DTOs on net10, plain sealed classes where `netstandard2.0` cannot express them.
- End every commit message with exactly:
  `Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>`
- Phases 3 and 4 (integrating TAG and the legacy scrapers) are **not** work in this repository — those are separate codebases. This plan ships the package and an integration guide.

---

## File Structure

All under `src/Clients/FS.Proxy.Client/` unless stated.

| File | Responsibility |
|---|---|
| `FS.Proxy.Client.csproj` | Multi-target, packable, folder-feed friendly. |
| `ProxyEndpoint.cs` | One usable proxy: address, credentials, `ToWebProxy()`. The only type legacy code touches. |
| `ProxyOutcome.cs` | The four outcomes, mirroring the server's `UsageEventOutcome`. |
| `ProxyOutcomeClassifier.cs` | Exceptions and status codes → outcome. The single most valuable file in the package. |
| `ProxyClientOptions.cs` | All tuning knobs, constructible by hand. |
| `ProxyTags.cs` | Typed constants for the seeded catalog; custom tags stay plain strings. |
| `Transport/ProxyServiceClient.cs` | The only type that speaks HTTP to the service. |
| `Transport/WireDtos.cs` | Request/response shapes for `/request` and `/feedback/batch`. |
| `Caching/IProxySnapshotCache.cs` | Seam, so Redis can be added later without touching the pool. |
| `Caching/FileSnapshotCache.cs` | JSON-on-disk snapshot cache with a TTL. |
| `Caching/NullSnapshotCache.cs` | For consumers that want memory only. |
| `Pool/ProxySnapshot.cs` | Immutable list + the instant it was fetched. |
| `Pool/ProxyPool.cs` | Refresh loop, jitter, quarantine, stale ceiling, local rotation. |
| `Feedback/FeedbackBuffer.cs` | Bounded queue, flush by size or interval, final flush on dispose. |
| `ProxySource.cs` | The facade all three adoption levels go through, plus the static `Instance` for DI-less consumers. |
| `Http/FsProxyRotationHandler.cs` | `DelegatingHandler` — adoption level 2. |
| `DependencyInjection/ServiceCollectionExtensions.cs` | net10-only `AddFsProxyClient` / `AddFsProxyRotation`. |
| `src/Tests/Proxy.Client.Tests/` | One test file per production file that has behavior. |
| `docs/integration/fs-proxy-client.md` | Integration guide for TAG and the legacy scrapers — the deliverable phases 3 and 4 consume. |

---

### Task 1: Project scaffolding that actually multi-targets

The whole plan rests on both target frameworks really building. The repo has no `netstandard2.0` precedent, and the inherited `TargetFramework` property silently defeats multi-targeting, so this task's deliverable is proof that both TFMs compile under the repo's analyzer settings.

**Files:**
- Create: `src/Clients/FS.Proxy.Client/FS.Proxy.Client.csproj`
- Create: `src/Clients/FS.Proxy.Client/ProxyOutcome.cs`
- Modify: `src/Directory.Packages.props`
- Modify: `src/FS.Proxy.slnx`
- Create: `src/Tests/Proxy.Client.Tests/Proxy.Client.Tests.csproj`
- Test: `src/Tests/Proxy.Client.Tests/ProxyOutcomeTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces, for every later task: a packable multi-targeted project, and `FSH.Proxy.Client.ProxyOutcome` — `{ Success, Failure, Banned, Timeout }`, mirroring the server's `FSH.Modules.Proxies.Contracts.UsageEventOutcome` member-for-member so the wire mapping is identity.

- [ ] **Step 1: Add the package version**

`System.Text.Json` is not yet in central package management. In `src/Directory.Packages.props`, add to the alphabetically appropriate `ItemGroup`:

```xml
<PackageVersion Include="System.Text.Json" Version="10.0.8" />
```

- [ ] **Step 2: Write the csproj**

`src/Clients/FS.Proxy.Client/FS.Proxy.Client.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <!-- Directory.Build.props sets the SINGULAR TargetFramework for everything under src/.
         With both properties set, MSBuild honours the singular one and silently builds net10
         only. Clearing it is what makes multi-targeting take effect. -->
    <TargetFramework></TargetFramework>
    <TargetFrameworks>netstandard2.0;net10.0</TargetFrameworks>

    <RootNamespace>FSH.Proxy.Client</RootNamespace>
    <AssemblyName>FS.Proxy.Client</AssemblyName>

    <IsPackable>true</IsPackable>
    <PackageId>FS.Proxy.Client</PackageId>
    <Version>0.1.0-preview.1</Version>
    <Description>Client SDK for the FS Proxy management service: pooled proxy leasing with outcome feedback, for .NET Framework 4.8 and .NET 10 scrapers.</Description>

    <!-- Directory.Build.targets turns on IncludeSymbols + snupkg for every packable project.
         A folder feed serves .nupkg only, so a .snupkg there is dead weight and gives no
         step-into debugging. Embedding the PDB in the DLL does. -->
    <IncludeSymbols>false</IncludeSymbols>
    <DebugType>embedded</DebugType>
    <EmbedAllSources>true</EmbedAllSources>
  </PropertyGroup>

  <!-- netstandard2.0 has no built-in System.Text.Json and no nullable attributes. -->
  <ItemGroup Condition="'$(TargetFramework)' == 'netstandard2.0'">
    <PackageReference Include="System.Text.Json" />
  </ItemGroup>

</Project>
```

- [ ] **Step 3: Write the one type this task ships**

`src/Clients/FS.Proxy.Client/ProxyOutcome.cs`:

```csharp
namespace FSH.Proxy.Client;

/// <summary>
/// The result of one attempt made through a proxy, as the service's policy engine understands it.
/// </summary>
/// <remarks>
/// Mirrors <c>FSH.Modules.Proxies.Contracts.UsageEventOutcome</c> member-for-member and in the same
/// order, so the wire mapping is the member name itself. The service serializes this enum as a
/// string, so renaming a member here is a breaking protocol change, not a refactor.
/// </remarks>
public enum ProxyOutcome
{
    /// <summary>The proxy did its job. The destination's own errors (4xx, 5xx) count as success.</summary>
    Success,

    /// <summary>The proxy is broken or misauthenticated — refused the connection, failed to resolve, or rejected the credentials.</summary>
    Failure,

    /// <summary>The destination recognized and rejected the proxy's IP.</summary>
    Banned,

    /// <summary>The proxy accepted the connection and never answered.</summary>
    Timeout,
}
```

- [ ] **Step 4: Write the test project**

`src/Tests/Proxy.Client.Tests/Proxy.Client.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

	<PropertyGroup>
		<RootNamespace>Proxy.Client.Tests</RootNamespace>
		<AssemblyName>Proxy.Client.Tests</AssemblyName>
		<IsPackable>false</IsPackable>
		<IsTestProject>true</IsTestProject>
		<NoWarn>$(NoWarn);CA1515;CA1861;CA1707;CA2000</NoWarn>
	</PropertyGroup>

	<ItemGroup>
		<PackageReference Include="Microsoft.NET.Test.Sdk" />
		<PackageReference Include="NSubstitute" />
		<PackageReference Include="Shouldly" />
		<PackageReference Include="xunit" />
		<PackageReference Include="xunit.runner.visualstudio" />
		<PackageReference Include="coverlet.collector" />
	</ItemGroup>

	<ItemGroup>
		<ProjectReference Include="..\..\Clients\FS.Proxy.Client\FS.Proxy.Client.csproj" />
		<!-- Referenced ONLY by the ProxyTags drift test (a later task), which compares the SDK's
		     constants against the server's seed catalog. -->
		<ProjectReference Include="..\..\Modules\Proxies\Modules.Proxies\Modules.Proxies.csproj" />
	</ItemGroup>

</Project>
```

- [ ] **Step 5: Write the failing test**

`src/Tests/Proxy.Client.Tests/ProxyOutcomeTests.cs`:

```csharp
using FSH.Modules.Proxies.Contracts;
using FSH.Proxy.Client;
using Shouldly;
using Xunit;

namespace Proxy.Client.Tests;

public sealed class ProxyOutcomeTests
{
    [Fact]
    public void ProxyOutcome_Should_Mirror_ServerUsageEventOutcome_MemberForMember()
    {
        // The service serializes UsageEventOutcome as a string (Program.cs registers
        // JsonStringEnumConverter), so the SDK's wire mapping is the member NAME. If the two
        // enums ever drift, feedback silently fails to deserialize server-side.
        var clientNames = Enum.GetNames<ProxyOutcome>();
        var serverNames = Enum.GetNames<UsageEventOutcome>();

        clientNames.ShouldBe(serverNames);
    }
}
```

- [ ] **Step 6: Run the test to verify it fails**

```bash
dotnet test src/Tests/Proxy.Client.Tests/Proxy.Client.Tests.csproj
```

Expected: FAIL — neither project is in the solution yet, so restore/build fails.

- [ ] **Step 7: Register both projects in the solution**

In `src/FS.Proxy.slnx`, add a `/Clients/` folder alongside the existing `/Modules/` and `/Host/` folders, and add the test project to the existing `/Tests/` folder:

```xml
  <Folder Name="/Clients/">
    <Project Path="Clients/FS.Proxy.Client/FS.Proxy.Client.csproj" />
  </Folder>
```

and inside `<Folder Name="/Tests/">`:

```xml
    <Project Path="Tests/Proxy.Client.Tests/Proxy.Client.Tests.csproj" />
```

- [ ] **Step 8: Prove BOTH target frameworks actually build**

This is the step that catches the silent single-target failure:

```bash
dotnet build src/Clients/FS.Proxy.Client/FS.Proxy.Client.csproj
ls src/Clients/FS.Proxy.Client/bin/Debug/
```

Expected: `ls` shows **both** `netstandard2.0/` and `net10.0/` directories. If only `net10.0` appears, the singular `TargetFramework` is still winning — fix that before continuing; every later task depends on it.

- [ ] **Step 9: Run the test and the full build**

```bash
dotnet test src/Tests/Proxy.Client.Tests/Proxy.Client.Tests.csproj
dotnet build src/FS.Proxy.slnx
```

Expected: 1 passed; solution builds with 0 warnings.

If the `netstandard2.0` target produces analyzer warnings the net10 target does not (likely candidates: CA1510 suggesting `ArgumentNullException.ThrowIfNull`, which does not exist there), add a **narrowly scoped** `NoWarn` to the csproj conditioned on that TFM, with a comment naming why. Do not disable analysis wholesale.

- [ ] **Step 10: Commit**

```bash
git add src/Clients/ src/Tests/Proxy.Client.Tests/ src/Directory.Packages.props src/FS.Proxy.slnx
git commit -m "feat(client): scaffold the multi-targeted FS.Proxy.Client package

netstandard2.0 for the .NET Framework 4.8 scrapers, net10.0 for TAG.
Clears the inherited singular TargetFramework, which would otherwise
make TargetFrameworks a no-op and build net10 only, in silence.

Ships ProxyOutcome with a test pinning it member-for-member against the
server's UsageEventOutcome: the service serializes that enum as a
string, so a rename on either side breaks feedback silently.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 2: Outcome classification

The reason the SDK exists. If each scraper decides independently whether a 503 blamed the proxy, the policy engine gets noise and disables healthy proxies.

**Files:**
- Create: `src/Clients/FS.Proxy.Client/ProxyOutcomeClassifier.cs`
- Test: `src/Tests/Proxy.Client.Tests/ProxyOutcomeClassifierTests.cs`

**Interfaces:**
- Consumes: `ProxyOutcome` (Task 1).
- Produces: `ProxyOutcomeClassifier.FromStatusCode(HttpStatusCode)`, `.FromException(Exception)`, `.FromResponse(HttpResponseMessage)` — all `static`, all returning `ProxyOutcome`.

- [ ] **Step 1: Write the failing test**

`src/Tests/Proxy.Client.Tests/ProxyOutcomeClassifierTests.cs`:

```csharp
using System.Net;
using FSH.Proxy.Client;
using Shouldly;
using Xunit;

namespace Proxy.Client.Tests;

public sealed class ProxyOutcomeClassifierTests
{
    [Theory]
    // The destination answered. The tunnel worked, whatever it said.
    [InlineData(HttpStatusCode.OK, ProxyOutcome.Success)]
    [InlineData(HttpStatusCode.NotFound, ProxyOutcome.Success)]
    [InlineData(HttpStatusCode.BadRequest, ProxyOutcome.Success)]
    [InlineData(HttpStatusCode.InternalServerError, ProxyOutcome.Success)]
    [InlineData(HttpStatusCode.BadGateway, ProxyOutcome.Success)]
    [InlineData(HttpStatusCode.ServiceUnavailable, ProxyOutcome.Success)]
    // The destination recognized and rejected the IP.
    [InlineData(HttpStatusCode.Forbidden, ProxyOutcome.Banned)]
    [InlineData((HttpStatusCode)429, ProxyOutcome.Banned)]
    // The proxy itself rejected us.
    [InlineData(HttpStatusCode.ProxyAuthenticationRequired, ProxyOutcome.Failure)]
    [InlineData(HttpStatusCode.RequestTimeout, ProxyOutcome.Timeout)]
    [InlineData(HttpStatusCode.GatewayTimeout, ProxyOutcome.Timeout)]
    public void FromStatusCode_Should_Blame_The_Right_Party(HttpStatusCode status, ProxyOutcome expected)
    {
        ProxyOutcomeClassifier.FromStatusCode(status).ShouldBe(expected);
    }

    [Fact]
    public void FromException_Should_Return_Timeout_For_A_Cancelled_Task_With_A_Timeout_Inner()
    {
        // This is exactly the shape WebScraper.GetStringAsync already catches: the proxy accepted
        // the connection and went silent until HttpClient.Timeout fired.
        var exception = new TaskCanceledException("timed out", new TimeoutException());

        ProxyOutcomeClassifier.FromException(exception).ShouldBe(ProxyOutcome.Timeout);
    }

    [Fact]
    public void FromException_Should_Return_Failure_For_A_Genuine_Cancellation()
    {
        // No TimeoutException inner: a shutting-down worker, not a bad proxy. Must NOT be
        // reported as Timeout or the policy engine punishes proxies for our own shutdowns.
        var exception = new TaskCanceledException("cancelled");

        ProxyOutcomeClassifier.FromException(exception).ShouldBe(ProxyOutcome.Failure);
    }

    [Theory]
    [InlineData(WebExceptionStatus.Timeout, ProxyOutcome.Timeout)]
    [InlineData(WebExceptionStatus.ConnectFailure, ProxyOutcome.Failure)]
    [InlineData(WebExceptionStatus.ProxyNameResolutionFailure, ProxyOutcome.Failure)]
    public void FromException_Should_Classify_WebException_For_The_Legacy_Scrapers(
        WebExceptionStatus status, ProxyOutcome expected)
    {
        // The 4.8 scrapers are on WebRequest and throw WebException, not HttpRequestException.
        var exception = new WebException("boom", status);

        ProxyOutcomeClassifier.FromException(exception).ShouldBe(expected);
    }

    [Fact]
    public void FromException_Should_Classify_A_WebException_Carrying_A_Response_By_Its_Status()
    {
        // A 403 arrives at a WebRequest caller as a WebException with ProtocolError, and the real
        // signal is the status code on the inner response. Reporting Failure here would blame the
        // proxy for being banned, which is a different remedy.
        var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
        var exception = new HttpRequestException("forbidden", null, HttpStatusCode.Forbidden);

        ProxyOutcomeClassifier.FromException(exception).ShouldBe(ProxyOutcome.Banned);
    }

    [Fact]
    public void FromResponse_Should_Defer_To_The_Status_Code_When_No_Custom_Classifier_Is_Given()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.Forbidden);

        ProxyOutcomeClassifier.FromResponse(response).ShouldBe(ProxyOutcome.Banned);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
dotnet test src/Tests/Proxy.Client.Tests/Proxy.Client.Tests.csproj --filter "FullyQualifiedName~ProxyOutcomeClassifierTests"
```

Expected: compilation FAILS — `ProxyOutcomeClassifier` does not exist.

- [ ] **Step 3: Write the classifier**

`src/Clients/FS.Proxy.Client/ProxyOutcomeClassifier.cs`:

```csharp
using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace FSH.Proxy.Client;

/// <summary>
/// Decides, for one failed or completed attempt, whether the PROXY failed or the DESTINATION failed.
/// </summary>
/// <remarks>
/// This is the reason the SDK exists as a shared package rather than a snippet in each scraper.
/// The service's policy engine disables a proxy once enough negative outcomes accumulate from
/// enough distinct reporters; if one scraper reports a portal's 503 as <see cref="ProxyOutcome.Failure"/>
/// and another reports it as <see cref="ProxyOutcome.Success"/>, the engine acts on noise — and the
/// failure mode is the expensive direction: a portal having a bad afternoon takes the whole pool
/// down with it.
///
/// The rule in one line: if the destination answered at all, the tunnel worked.
/// </remarks>
public static class ProxyOutcomeClassifier
{
    /// <summary>Classifies a response that actually arrived, by its status code.</summary>
    public static ProxyOutcome FromStatusCode(HttpStatusCode statusCode) => statusCode switch
    {
        // The destination recognized and rejected the proxy's IP. A different proxy may work.
        HttpStatusCode.Forbidden => ProxyOutcome.Banned,
        (HttpStatusCode)429 => ProxyOutcome.Banned,

        // The PROXY rejected us — it wanted credentials we did not supply or that were wrong.
        HttpStatusCode.ProxyAuthenticationRequired => ProxyOutcome.Failure,

        // Something upstream timed out. Treated as a proxy-side stall.
        HttpStatusCode.RequestTimeout => ProxyOutcome.Timeout,
        HttpStatusCode.GatewayTimeout => ProxyOutcome.Timeout,

        // Everything else — 2xx, 3xx, 404, 400, 500, 502, 503 — means the destination answered.
        // The proxy did its job; the site's own problems are not the proxy's fault.
        _ => ProxyOutcome.Success,
    };

    /// <summary>Classifies a response, optionally consulting a caller-supplied content inspector first.</summary>
    /// <param name="response">The response to classify.</param>
    /// <param name="inspect">
    /// Optional. Lets the caller recognize a soft block — a destination returning HTTP 200 with a
    /// captcha or robot-check page. No generic rule can detect that, and it is the single most
    /// valuable signal a scraper has. Return <c>null</c> to fall through to the status code.
    /// </param>
    public static ProxyOutcome FromResponse(HttpResponseMessage response, Func<HttpResponseMessage, ProxyOutcome?>? inspect = null)
    {
        if (response is null) throw new ArgumentNullException(nameof(response));

        var inspected = inspect?.Invoke(response);
        return inspected ?? FromStatusCode(response.StatusCode);
    }

    /// <summary>Classifies a thrown exception.</summary>
    public static ProxyOutcome FromException(Exception exception)
    {
        if (exception is null) throw new ArgumentNullException(nameof(exception));

        switch (exception)
        {
            // HttpClient's timeout surfaces as a cancelled task whose inner is a TimeoutException.
            // A cancellation WITHOUT that inner is our own shutdown — never blame a proxy for it.
            case TaskCanceledException taskCanceled:
                return taskCanceled.InnerException is TimeoutException ? ProxyOutcome.Timeout : ProxyOutcome.Failure;

            case TimeoutException:
                return ProxyOutcome.Timeout;

            // Carries a status code from .NET 5 onward; on netstandard2.0 StatusCode is always null,
            // so this falls through to Failure and the caller should prefer FromResponse there.
            case HttpRequestException httpRequest when httpRequest.StatusCode.HasValue:
                return FromStatusCode(httpRequest.StatusCode.Value);

            // The 4.8 scrapers are on WebRequest and see WebException, not HttpRequestException.
            case WebException webException:
                return FromWebException(webException);

            default:
                return ProxyOutcome.Failure;
        }
    }

    private static ProxyOutcome FromWebException(WebException exception)
    {
        if (exception.Status == WebExceptionStatus.Timeout)
        {
            return ProxyOutcome.Timeout;
        }

        // ProtocolError means the destination answered with an error status — the status is the
        // real signal, not the exception. A 403 here is Banned, not a broken proxy.
        if (exception.Status == WebExceptionStatus.ProtocolError && exception.Response is HttpWebResponse response)
        {
            return FromStatusCode(response.StatusCode);
        }

        return ProxyOutcome.Failure;
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

```bash
dotnet test src/Tests/Proxy.Client.Tests/Proxy.Client.Tests.csproj --filter "FullyQualifiedName~ProxyOutcomeClassifierTests"
```

Expected: all passing. If the `netstandard2.0` target rejects `HttpRequestException.StatusCode` (it does not exist there), guard that `case` with `#if NET` and add a test note — do not delete the branch from the net10 target.

- [ ] **Step 5: Build both targets and commit**

```bash
dotnet build src/FS.Proxy.slnx
git add src/Clients/FS.Proxy.Client/ProxyOutcomeClassifier.cs src/Tests/Proxy.Client.Tests/ProxyOutcomeClassifierTests.cs
git commit -m "feat(client): classify whether the proxy or the destination failed

The rule in one line: if the destination answered at all, the tunnel
worked. 404/500/502/503 are Success — reporting them as Failure is what
takes a whole pool down when a portal has a bad afternoon.

Handles both exception families: TaskCanceledException+TimeoutException
from HttpClient, and WebException from the 4.8 scrapers still on
WebRequest. A cancellation with no TimeoutException inner is our own
shutdown and is never blamed on a proxy.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 3: `ProxyEndpoint`, options, and the tag catalog

The value types every other task hands around, plus the constants that stop anyone typing `"source:chile - mercado publico"` by hand.

**Files:**
- Create: `src/Clients/FS.Proxy.Client/ProxyEndpoint.cs`
- Create: `src/Clients/FS.Proxy.Client/ProxyClientOptions.cs`
- Create: `src/Clients/FS.Proxy.Client/ProxyTags.cs`
- Test: `src/Tests/Proxy.Client.Tests/ProxyEndpointTests.cs`
- Test: `src/Tests/Proxy.Client.Tests/ProxyTagsTests.cs`

**Interfaces:**
- Produces, for every later task:
  - `ProxyEndpoint(Guid id, string host, int port, string? username, string? password)` with `Id`, `Host`, `Port`, `Username`, `Password`, `ToWebProxy()`, and a `ToString()` that masks the password.
  - `ProxyClientOptions` with `BaseAddress`, `ApiKey`, `PoolSize` (default 50), `RefreshInterval` (90 s), `RefreshJitterPercent` (20), `StaleCeiling` (10 min), `Quarantine` (2 min), `FeedbackFlushInterval` (10 s), `FeedbackBatchSize` (50), `FeedbackQueueCapacity` (10_000), `SuccessSampling` (0.0), `Validate()`.
  - `ProxyTags` with nested `Country`, `Source`, `EntityType`, `OperationType`, `Application` classes of `const string`, plus `ProxyTags.Of(category, value)` and `ProxyTags.Normalize(tag)`.

- [ ] **Step 1: Write the failing tests**

`src/Tests/Proxy.Client.Tests/ProxyEndpointTests.cs`:

```csharp
using FSH.Proxy.Client;
using Shouldly;
using Xunit;

namespace Proxy.Client.Tests;

public sealed class ProxyEndpointTests
{
    private static ProxyEndpoint Sample(string? user = "u", string? password = "s3cret") =>
        new(Guid.NewGuid(), "203.0.113.10", 8080, user, password);

    [Fact]
    public void ToWebProxy_Should_Carry_Address_And_Credentials()
    {
        var endpoint = Sample();

        var webProxy = endpoint.ToWebProxy();

        webProxy.ShouldNotBeNull();
        var credential = webProxy.Credentials.ShouldBeOfType<System.Net.NetworkCredential>();
        credential.UserName.ShouldBe("u");
        credential.Password.ShouldBe("s3cret");
    }

    [Fact]
    public void ToWebProxy_Should_Omit_Credentials_When_The_Proxy_Is_Open()
    {
        var endpoint = Sample(user: null, password: null);

        endpoint.ToWebProxy().Credentials.ShouldBeNull();
    }

    [Fact]
    public void ToString_Should_Never_Reveal_The_Password()
    {
        // /request returns passwords in the clear. They live in memory and on the snapshot cache,
        // and must not reach a log through a careless interpolation of the endpoint.
        var endpoint = Sample();

        var text = endpoint.ToString();

        text.ShouldNotContain("s3cret");
        text.ShouldContain("203.0.113.10");
    }
}
```

`src/Tests/Proxy.Client.Tests/ProxyTagsTests.cs`:

```csharp
using FSH.Modules.Proxies.Data;
using FSH.Proxy.Client;
using Shouldly;
using Xunit;

namespace Proxy.Client.Tests;

public sealed class ProxyTagsTests
{
    [Fact]
    public void Constants_Should_Already_Be_Normalized()
    {
        // Tag.Normalize on the server is trim + lowercase. A constant that is not already in that
        // form would still match (the SDK normalizes on the way out) but would read as a lie.
        ProxyTags.Country.Chile.ShouldBe("country:cl");
        ProxyTags.EntityType.Tender.ShouldBe("entitytype:tender");
        ProxyTags.OperationType.Attachments.ShouldBe("operationtype:attachments");
    }

    [Fact]
    public void Normalize_Should_Match_The_Server_Rule()
    {
        ProxyTags.Normalize("  EntityType:Tender  ").ShouldBe("entitytype:tender");
    }

    [Fact]
    public void Of_Should_Compose_And_Normalize()
    {
        ProxyTags.Of("EntityType", "Tender").ShouldBe("entitytype:tender");
    }

    [Fact]
    public void Every_Seeded_Catalog_Value_Should_Have_A_Constant()
    {
        // The SDK targets netstandard2.0 and cannot reference Modules.Proxies, so these constants
        // are hand-maintained. This test is the only thing stopping them drifting from the seed.
        var expected = TagCategorySeedData.Categories
            .SelectMany(category => category.Values.Select(value => ProxyTags.Of(category.Name, value)))
            .OrderBy(tag => tag, StringComparer.Ordinal)
            .ToList();

        var actual = ProxyTags.All.OrderBy(tag => tag, StringComparer.Ordinal).ToList();

        actual.ShouldBe(expected);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test src/Tests/Proxy.Client.Tests/Proxy.Client.Tests.csproj --filter "FullyQualifiedName~ProxyEndpointTests|FullyQualifiedName~ProxyTagsTests"
```

Expected: compilation FAILS — none of the three types exist.

- [ ] **Step 3: Write `ProxyEndpoint`**

`src/Clients/FS.Proxy.Client/ProxyEndpoint.cs`:

```csharp
using System;
using System.Globalization;
using System.Net;

namespace FSH.Proxy.Client;

/// <summary>
/// One usable proxy. The only type a legacy <c>WebRequest</c>-based scraper needs to touch.
/// </summary>
public sealed class ProxyEndpoint
{
    public ProxyEndpoint(Guid id, string host, int port, string? username, string? password)
    {
        if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("Host is required.", nameof(host));

        Id = id;
        Host = host.Trim();
        Port = port;
        Username = username;
        Password = password;
    }

    /// <summary>The service's identifier for this proxy. Required to report an outcome against it.</summary>
    public Guid Id { get; }

    public string Host { get; }
    public int Port { get; }
    public string? Username { get; }
    public string? Password { get; }

    /// <summary>Builds an <see cref="IWebProxy"/> for <c>HttpClientHandler.Proxy</c> or <c>WebRequest.Proxy</c>.</summary>
    public IWebProxy ToWebProxy()
    {
        var proxy = new WebProxy(Host, Port);
        if (!string.IsNullOrEmpty(Username))
        {
            proxy.Credentials = ToCredential();
        }
        return proxy;
    }

    public NetworkCredential ToCredential() => new(Username, Password);

    /// <summary>
    /// Deliberately masks the password. <c>/request</c> returns credentials in the clear, and the
    /// most likely way they reach a log file is someone interpolating an endpoint into a message.
    /// </summary>
    public override string ToString() =>
        string.Format(CultureInfo.InvariantCulture, "{0}:{1} ({2})", Host, Port,
            string.IsNullOrEmpty(Username) ? "anonymous" : Username + ":***");
}
```

- [ ] **Step 4: Write `ProxyClientOptions`**

`src/Clients/FS.Proxy.Client/ProxyClientOptions.cs`:

```csharp
using System;

namespace FSH.Proxy.Client;

/// <summary>
/// Every knob the client exposes. Constructible by hand on purpose: the .NET Framework 4.8 scrapers
/// may have no <c>IConfiguration</c> at all, so binding is a net10 convenience, never a requirement.
/// </summary>
public sealed class ProxyClientOptions
{
    /// <summary>Root of the proxy service, e.g. <c>https://proxy-qa.falconsoft.cl</c>.</summary>
    public Uri? BaseAddress { get; set; }

    /// <summary>The scraper's own API key. Supply from an environment variable, never from a config file.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Tags this client leases against when none are passed explicitly.</summary>
    public string[] Tags { get; set; } = Array.Empty<string>();

    /// <summary>
    /// How many proxies to hold locally per tag set. Hard ceiling of 50: the service's
    /// RequestProxiesQueryValidator caps <c>count</c> there, and a larger value is silently
    /// truncated rather than rejected.
    /// </summary>
    public int PoolSize { get; set; } = 50;

    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromSeconds(90);

    /// <summary>Spread of the refresh interval, so N scrapers starting together do not synchronize.</summary>
    public int RefreshJitterPercent { get; set; } = 20;

    /// <summary>
    /// How long a stale snapshot keeps being served when the service is unreachable. Past this, the
    /// client fails loudly. Without it a 30-second network blip strands every scraper at once.
    /// </summary>
    public TimeSpan StaleCeiling { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>How long a locally-failed proxy is set aside before being offered again.</summary>
    public TimeSpan Quarantine { get; set; } = TimeSpan.FromMinutes(2);

    public TimeSpan FeedbackFlushInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Events per flush. The service's batch endpoint caps a submission at 200.</summary>
    public int FeedbackBatchSize { get; set; } = 50;

    /// <summary>Bound on the in-memory feedback queue. Overflow drops rather than blocking a scrape.</summary>
    public int FeedbackQueueCapacity { get; set; } = 10_000;

    /// <summary>
    /// Fraction of Success outcomes actually transmitted, 0 to 1. Defaults to 0 because the
    /// service's PolicyEvaluationService counts only non-Success events — successes are rows no
    /// decision reads. Raise it only if you want the server-side series for its own sake.
    /// </summary>
    public double SuccessSampling { get; set; }

    public void Validate()
    {
        if (BaseAddress is null) throw new InvalidOperationException($"{nameof(BaseAddress)} is required.");
        if (string.IsNullOrWhiteSpace(ApiKey)) throw new InvalidOperationException($"{nameof(ApiKey)} is required.");
        if (PoolSize < 1 || PoolSize > 50) throw new InvalidOperationException($"{nameof(PoolSize)} must be between 1 and 50.");
        if (FeedbackBatchSize < 1 || FeedbackBatchSize > 200) throw new InvalidOperationException($"{nameof(FeedbackBatchSize)} must be between 1 and 200.");
        if (SuccessSampling < 0 || SuccessSampling > 1) throw new InvalidOperationException($"{nameof(SuccessSampling)} must be between 0 and 1.");
        if (RefreshJitterPercent < 0 || RefreshJitterPercent > 100) throw new InvalidOperationException($"{nameof(RefreshJitterPercent)} must be between 0 and 100.");
    }
}
```

- [ ] **Step 5: Write `ProxyTags`**

Generate the constants from `src/Modules/Proxies/Modules.Proxies/Data/TagCategorySeedData.cs` — read that file and transcribe every value. Identifier names are the value with non-alphanumeric characters removed and each word capitalized (`"Argentina - Comprar - Garrahan"` → `ArgentinaComprarGarrahan`). The constant's **value** is always `ProxyTags.Of(category, value)` of the real seed strings, spaces and all.

`src/Clients/FS.Proxy.Client/ProxyTags.cs` — skeleton, with the full member lists filled in from the seed:

```csharp
using System;
using System.Collections.Generic;

namespace FSH.Proxy.Client;

/// <summary>
/// The tags seeded in the service's reference catalog, as constants.
/// </summary>
/// <remarks>
/// These are plain <c>const string</c> on purpose. Tags are free-form on the server, so a custom tag
/// must stay a first-class citizen — <c>GetProxies(ProxyTags.Country.Chile, "experimento:foo")</c>
/// with no ceremony. Any closed type (an enum, a wrapper struct) would demote custom tags to
/// <c>ProxyTag.Custom("…")</c>, which is exactly what this design rejects.
///
/// Hand-maintained, because the SDK targets netstandard2.0 and cannot reference the server's
/// TagCategorySeedData. ProxyTagsTests.Every_Seeded_Catalog_Value_Should_Have_A_Constant is what
/// keeps them honest.
/// </remarks>
public static class ProxyTags
{
    public static class Country
    {
        public const string Argentina = "country:ar";
        public const string Bolivia = "country:bo";
        public const string Chile = "country:cl";
        public const string Colombia = "country:co";
        public const string Ecuador = "country:ec";
        public const string Guatemala = "country:gt";
        public const string Mexico = "country:mx";
        public const string Peru = "country:pe";
        public const string Uruguay = "country:uy";
    }

    public static class EntityType
    {
        public const string Tender = "entitytype:tender";
        public const string PurchaseOrder = "entitytype:purchaseorder";
        // … transcribe the remaining entityType values from the seed
    }

    public static class OperationType
    {
        public const string Attachments = "operationtype:attachments";
    }

    public static class Source
    {
        public const string ChileMercadoPublico = "source:chile - mercado publico";
        // … transcribe the remaining source values from the seed, spaces and all
    }

    public static class Application
    {
        public const string Tag = "application:tag";
        // … transcribe the remaining application values from the seed
    }

    /// <summary>Every constant above, for the drift test and for anyone who wants to enumerate.</summary>
    public static IReadOnlyList<string> All { get; } = new[]
    {
        // … every constant, in any order — the drift test sorts before comparing
    };

    /// <summary>Composes a tag from a category and a value, normalized the way the server will.</summary>
    public static string Of(string category, string value)
    {
        if (string.IsNullOrWhiteSpace(category)) throw new ArgumentException("Category is required.", nameof(category));
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Value is required.", nameof(value));

        return Normalize(category.Trim() + ":" + value.Trim());
    }

    /// <summary>
    /// Mirrors the server's <c>Tag.Normalize</c> exactly: trim, then lowercase with the invariant
    /// culture. Applied to every tag the client sends, so callers may pass either form.
    /// </summary>
    public static string Normalize(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) throw new ArgumentException("Tag is required.", nameof(tag));

        return tag.Trim().ToLowerInvariant();
    }
}
```

- [ ] **Step 6: Run the tests to verify they pass**

```bash
dotnet test src/Tests/Proxy.Client.Tests/Proxy.Client.Tests.csproj
```

Expected: all passing. The drift test failing means a seed value has no constant — add it; do not weaken the test.

- [ ] **Step 7: Build and commit**

```bash
dotnet build src/FS.Proxy.slnx
git add src/Clients/FS.Proxy.Client/ src/Tests/Proxy.Client.Tests/
git commit -m "feat(client): add ProxyEndpoint, options, and the seeded tag catalog

Tags ship as const string so a custom tag stays first-class — any closed
type would demote it to ProxyTag.Custom(). A drift test compares the
constants against the server's TagCategorySeedData, which is the only
thing keeping them in sync across the netstandard2.0 boundary.

ProxyEndpoint.ToString() masks the password: /request returns them in
the clear, and the likeliest leak is an interpolation into a log line.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 4: HTTP transport

The only type that speaks to the service. Isolated so every other test can fake it.

**Files:**
- Create: `src/Clients/FS.Proxy.Client/Transport/WireDtos.cs`
- Create: `src/Clients/FS.Proxy.Client/Transport/IProxyServiceClient.cs`
- Create: `src/Clients/FS.Proxy.Client/Transport/ProxyServiceClient.cs`
- Test: `src/Tests/Proxy.Client.Tests/Transport/ProxyServiceClientTests.cs`
- Test: `src/Tests/Proxy.Client.Tests/Transport/StubHandler.cs`

**Interfaces:**
- Consumes: `ProxyEndpoint`, `ProxyOutcome`, `ProxyClientOptions`.
- Produces:
  - `IProxyServiceClient` with `Task<IReadOnlyList<ProxyEndpoint>> RequestAsync(IReadOnlyList<string> tags, int count, CancellationToken ct)` and `Task RequestFeedbackAsync(IReadOnlyList<FeedbackItem> events, CancellationToken ct)`.
  - `FeedbackItem(Guid ProxyId, ProxyOutcome Outcome, string? Detail)`.

- [ ] **Step 1: Write the stub handler and the failing tests**

`src/Tests/Proxy.Client.Tests/Transport/StubHandler.cs`:

```csharp
using System.Net;

namespace Proxy.Client.Tests.Transport;

/// <summary>Records the request it was given and returns a canned response.</summary>
internal sealed class StubHandler : HttpMessageHandler
{
    private readonly HttpStatusCode _status;
    private readonly string _body;

    public StubHandler(HttpStatusCode status, string body)
    {
        _status = status;
        _body = body;
    }

    public HttpRequestMessage? LastRequest { get; private set; }
    public string? LastRequestBody { get; private set; }
    public int CallCount { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        CallCount++;
        LastRequest = request;
        if (request.Content is not null)
        {
            LastRequestBody = await request.Content.ReadAsStringAsync().ConfigureAwait(false);
        }

        return new HttpResponseMessage(_status) { Content = new StringContent(_body) };
    }
}
```

`src/Tests/Proxy.Client.Tests/Transport/ProxyServiceClientTests.cs`:

```csharp
using System.Net;
using FSH.Proxy.Client;
using FSH.Proxy.Client.Transport;
using Shouldly;
using Xunit;

namespace Proxy.Client.Tests.Transport;

public sealed class ProxyServiceClientTests
{
    private static ProxyClientOptions Options() => new()
    {
        BaseAddress = new Uri("https://proxy.test"),
        ApiKey = "fsh_proxies_deadbeef",
    };

    [Fact]
    public async Task RequestAsync_Should_Send_The_ApiKey_Header_And_Normalized_Tags()
    {
        const string body = """
        [{"id":"11111111-1111-1111-1111-111111111111","host":"203.0.113.10","port":8080,"protocol":"Http","username":"u","password":"p"}]
        """;
        var handler = new StubHandler(HttpStatusCode.OK, body);
        using var http = new HttpClient(handler);
        var sut = new ProxyServiceClient(http, Options());

        var result = await sut.RequestAsync(["Country:CL", " entityType:Tender "], 25, CancellationToken.None);

        handler.LastRequest!.Headers.GetValues("X-Api-Key").ShouldBe(["fsh_proxies_deadbeef"]);
        handler.LastRequest.RequestUri!.AbsolutePath.ShouldBe("/api/v1/proxies/request");
        // Tags must go out normalized — the server lowercases on its side, but sending the raw
        // form makes the request body a poor match for what the admin UI shows.
        handler.LastRequestBody.ShouldContain("country:cl");
        handler.LastRequestBody.ShouldContain("entitytype:tender");
        result.Count.ShouldBe(1);
        result[0].Host.ShouldBe("203.0.113.10");
        result[0].Password.ShouldBe("p");
    }

    [Fact]
    public async Task RequestAsync_Should_Return_Empty_When_The_Service_Says_No_Proxies_Match()
    {
        // The service answers 404 with a ProblemDetails when no Active proxy matches the tags.
        // That is an ordinary, expected state — not an exception — because the caller's fallback
        // (keep serving the stale snapshot) is the same either way.
        var handler = new StubHandler(HttpStatusCode.NotFound, """{"title":"No active proxies match the requested tags."}""");
        using var http = new HttpClient(handler);
        var sut = new ProxyServiceClient(http, Options());

        var result = await sut.RequestAsync(["country:cl"], 25, CancellationToken.None);

        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task RequestAsync_Should_Throw_On_An_Unauthorized_Response()
    {
        // A bad API key is a misconfiguration the operator must see, not something to swallow.
        var handler = new StubHandler(HttpStatusCode.Unauthorized, "");
        using var http = new HttpClient(handler);
        var sut = new ProxyServiceClient(http, Options());

        await Should.ThrowAsync<HttpRequestException>(
            () => sut.RequestAsync(["country:cl"], 25, CancellationToken.None));
    }

    [Fact]
    public async Task RequestFeedbackAsync_Should_Post_Outcomes_As_Strings()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"accepted":1,"rejected":[]}""");
        using var http = new HttpClient(handler);
        var sut = new ProxyServiceClient(http, Options());
        var proxyId = Guid.NewGuid();

        await sut.RequestFeedbackAsync(
            [new FeedbackItem(proxyId, ProxyOutcome.Banned, "mercadopublico:robot-check")],
            CancellationToken.None);

        handler.LastRequest!.RequestUri!.AbsolutePath.ShouldBe("/api/v1/proxies/feedback/batch");
        // The service registers JsonStringEnumConverter, so the outcome must go out as a NAME.
        // Sending the numeric value deserializes to the wrong member without any error.
        handler.LastRequestBody.ShouldContain("\"Banned\"");
        handler.LastRequestBody.ShouldContain(proxyId.ToString());
    }

    [Fact]
    public async Task RequestFeedbackAsync_Should_Not_Call_The_Service_For_An_Empty_Batch()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "");
        using var http = new HttpClient(handler);
        var sut = new ProxyServiceClient(http, Options());

        await sut.RequestFeedbackAsync([], CancellationToken.None);

        handler.CallCount.ShouldBe(0);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test src/Tests/Proxy.Client.Tests/Proxy.Client.Tests.csproj --filter "FullyQualifiedName~ProxyServiceClientTests"
```

Expected: compilation FAILS.

- [ ] **Step 3: Implement the transport**

Write `WireDtos.cs` (the JSON shapes: a response item with `id`/`host`/`port`/`protocol`/`username`/`password`, a request body with `tags`/`count`/`strategy`/`sessionId`, a feedback body with `events`), `IProxyServiceClient.cs`, and `ProxyServiceClient.cs`.

Requirements the tests above pin, plus these:

- Serialize with `JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new JsonStringEnumConverter() } }`. The enum converter is **not** optional — the service registers one, so numeric enums deserialize to the wrong member silently.
- Send `strategy` as `"Random"` for pool fills. Never `"RoundRobin"`: that cursor is global per tag-set on the server and shared by every consumer, so filling pools through it turns it into noise. Rotation is local.
- `RequestAsync` clamps `count` to 50.
- 404 → empty list. Any other non-success → `EnsureSuccessStatusCode()`.
- One `static readonly JsonSerializerOptions` instance, not one per call (CA1869 will fail the build otherwise).

- [ ] **Step 4: Run the tests and build**

```bash
dotnet test src/Tests/Proxy.Client.Tests/Proxy.Client.Tests.csproj --filter "FullyQualifiedName~ProxyServiceClientTests"
dotnet build src/FS.Proxy.slnx
```

Expected: 5 passing; 0 warnings on both targets.

- [ ] **Step 5: Commit**

```bash
git add src/Clients/FS.Proxy.Client/Transport/ src/Tests/Proxy.Client.Tests/Transport/
git commit -m "feat(client): add the HTTP transport for /request and /feedback/batch

Fills pools with strategy=Random, never RoundRobin: that cursor is
global per tag-set on the server, so ten scrapers refreshing through it
would turn it into noise. Rotation is local.

404 from /request means no proxy matches the tags — an ordinary state,
returned as an empty list, because the caller's fallback is the same
either way. A 401 still throws: a bad API key is a misconfiguration the
operator has to see.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 5: Snapshot cache

Why it exists: the scrapers are batch processes that start and die. If the service is unreachable at startup, the process does not start at all — a real availability regression against the hardcoded list it replaces.

**Files:**
- Create: `src/Clients/FS.Proxy.Client/Caching/IProxySnapshotCache.cs`
- Create: `src/Clients/FS.Proxy.Client/Caching/NullSnapshotCache.cs`
- Create: `src/Clients/FS.Proxy.Client/Caching/FileSnapshotCache.cs`
- Modify: `src/Clients/FS.Proxy.Client/ProxyClientOptions.cs` (add the `SnapshotCache` property)
- Test: `src/Tests/Proxy.Client.Tests/Caching/FileSnapshotCacheTests.cs`

**Interfaces:**
- Produces: `IProxySnapshotCache` with `IReadOnlyList<ProxyEndpoint>? Read(string key)` and `void Write(string key, IReadOnlyList<ProxyEndpoint> endpoints)` — both synchronous, both required never to throw. `FileSnapshotCache(string directory, TimeSpan ttl)`. `NullSnapshotCache.Instance`.
- **Also modifies `ProxyClientOptions` (Task 3)** to add `public IProxySnapshotCache? SnapshotCache { get; set; }`, defaulting to null. The property lives here, not in Task 3, because Task 3 runs first and the interface does not exist yet — declaring it there would not compile.

- [ ] **Step 1: Write the failing tests**

`src/Tests/Proxy.Client.Tests/Caching/FileSnapshotCacheTests.cs`:

```csharp
using FSH.Proxy.Client;
using FSH.Proxy.Client.Caching;
using Shouldly;
using Xunit;

namespace Proxy.Client.Tests.Caching;

public sealed class FileSnapshotCacheTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "fsproxy-cache-" + Guid.NewGuid().ToString("N"));

    private static IReadOnlyList<ProxyEndpoint> Sample() =>
        [new ProxyEndpoint(Guid.NewGuid(), "203.0.113.10", 8080, "u", "p")];

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
```

- [ ] **Step 2: Run to verify failure, then implement**

```bash
dotnet test src/Tests/Proxy.Client.Tests/Proxy.Client.Tests.csproj --filter "FullyQualifiedName~FileSnapshotCacheTests"
```

Then write the three files. Requirements:

- **Neither `Read` nor `Write` may ever throw.** Wrap everything; on any failure `Read` returns `null` and `Write` does nothing. This is the whole point: the cache is a fallback, and a fallback that can fail the process is worse than no fallback.
- The file name is a hash of the key (the key contains `:` and spaces), so tag sets never collide and the name is filesystem-safe.
- The stored document carries a `fetchedAtUtc` alongside the endpoints; `Read` compares it against the TTL.
- **Credentials are stored in plaintext, by explicit decision** — record it in the XML doc: these are internal systems, and the status quo it replaces is provider credentials committed to source control. Say plainly that the file must live in a runtime directory and never beside config, and must be in `.gitignore`.
- Write via a temp file then move, so a crash mid-write leaves the previous snapshot rather than a truncated one.

- [ ] **Step 3: Verify, build, commit**

```bash
dotnet test src/Tests/Proxy.Client.Tests/Proxy.Client.Tests.csproj --filter "FullyQualifiedName~FileSnapshotCacheTests"
dotnet build src/FS.Proxy.slnx
git add src/Clients/FS.Proxy.Client/Caching/ src/Tests/Proxy.Client.Tests/Caching/
git commit -m "feat(client): add the file snapshot cache

Scrapers are batch processes: without a cache, a service outage at
startup means the process does not start at all, which the hardcoded
list it replaces never did.

Read and Write never throw — a corrupt or unwritable cache degrades to
'no cache', because a fallback that can fail the process is worse than
no fallback. Writes go through a temp file and a move, so a crash
mid-write leaves the previous snapshot rather than a truncated one.

Credentials are stored in plaintext by explicit decision; the file
belongs in a runtime directory and in .gitignore.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 6: The pool

The design-heavy task. Everything else is scaffolding around this.

**Files:**
- Create: `src/Clients/FS.Proxy.Client/Pool/ProxySnapshot.cs`
- Create: `src/Clients/FS.Proxy.Client/Pool/ProxyPool.cs`
- Test: `src/Tests/Proxy.Client.Tests/Pool/ProxyPoolTests.cs`

**Interfaces:**
- Consumes: `IProxyServiceClient` (Task 4), `IProxySnapshotCache` (Task 5), `ProxyClientOptions`, `ProxyEndpoint`, `ProxyOutcome`.
- Produces: `ProxyPool(IProxyServiceClient client, ProxyClientOptions options, IReadOnlyList<string> tags, Func<DateTimeOffset>? clock = null)` with `Task WarmupAsync(CancellationToken)`, `ProxyEndpoint? Next()`, `void Quarantine(Guid proxyId)`, `Task RefreshAsync(CancellationToken)`, `int HealthyCount`, `bool IsStale`.

The injected `clock` is what makes quarantine expiry and the stale ceiling testable without `Thread.Sleep`.

- [ ] **Step 1: Write the failing tests**

These behaviors must each have a test. Write them against a `Substitute.For<IProxyServiceClient>()` and a controllable clock:

1. `WarmupAsync` fills from the service and `Next()` then returns a proxy.
2. `Next()` rotates: over N calls against a pool of N, every proxy is returned exactly once.
3. A quarantined proxy is not returned while the cooldown is live.
4. Once the cooldown expires (advance the clock), it is returned again.
5. **With every proxy quarantined, `Next()` still returns one** rather than null — a questionable proxy beats failing the scrape.
6. When `RefreshAsync` gets an empty list (service 404), the previous snapshot keeps serving.
7. Past `StaleCeiling`, `IsStale` is true and `Next()` returns null.
8. A successful refresh clears staleness and replaces the snapshot wholesale.
9. `WarmupAsync` falls back to the cache when the service throws, and `Next()` serves the cached proxies.
10. `WarmupAsync` writes a successful fetch to the cache.
11. Refresh replaces the snapshot atomically — a proxy removed server-side stops being returned.

- [ ] **Step 2: Run to verify failure, then implement**

Requirements beyond the tests:

- The snapshot is an immutable `ProxySnapshot` swapped with a single reference assignment. `Next()` never locks.
- Rotation uses `Interlocked.Increment` over a monotonically increasing counter, modulo the snapshot length.
- Quarantine is a `ConcurrentDictionary<Guid, DateTimeOffset>` of expiry instants, pruned lazily.
- `RefreshAsync` clamps `count` to `PoolSize`, catches **all** exceptions from the transport, and on failure leaves the snapshot in place.
- Jitter: the next refresh delay is `RefreshInterval * (1 ± RefreshJitterPercent/100)`, drawn per refresh. `Random.Shared` does not exist on `netstandard2.0` — use a `[ThreadStatic]` `Random` or a single locked instance, and suppress CA5394 with a justification (this is load spreading, not a security decision).
- The background timer lives in `ProxySource` (Task 8), not here. `ProxyPool` exposes `RefreshAsync` and stays passive, so every test is deterministic.

- [ ] **Step 3: Verify, build, commit**

```bash
dotnet test src/Tests/Proxy.Client.Tests/Proxy.Client.Tests.csproj --filter "FullyQualifiedName~ProxyPoolTests"
dotnet build src/FS.Proxy.slnx
git add src/Clients/FS.Proxy.Client/Pool/ src/Tests/Proxy.Client.Tests/Pool/
git commit -m "feat(client): add the proxy pool

Immutable snapshot swapped by reference, so the read path never locks
and never does I/O — the consuming scrapers are full of .Result and an
async read would deadlock them.

Degrades rather than failing: a 404 or an unreachable service keeps the
previous snapshot serving until StaleCeiling, because a 30-second blip
should not strand every scraper at once. With every proxy quarantined it
still hands one out — a questionable proxy beats throwing.

Clock is injected so quarantine expiry and staleness are tested without
sleeping.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 7: Feedback buffer

**Files:**
- Create: `src/Clients/FS.Proxy.Client/Feedback/FeedbackBuffer.cs`
- Test: `src/Tests/Proxy.Client.Tests/Feedback/FeedbackBufferTests.cs`

**Interfaces:**
- Consumes: `IProxyServiceClient`, `FeedbackItem`, `ProxyClientOptions`.
- Produces: `FeedbackBuffer(IProxyServiceClient client, ProxyClientOptions options)` with `void Enqueue(Guid proxyId, ProxyOutcome outcome, string? detail)`, `Task FlushAsync(CancellationToken)`, `long DroppedCount`, and `IAsyncDisposable`/`IDisposable` performing a final flush.

- [ ] **Step 1: Write the failing tests**

Each of these is a required test:

1. `Enqueue` then `FlushAsync` sends the events once.
2. **`Success` is not transmitted at default settings** (`SuccessSampling = 0`) — the service's policy engine ignores successes, so they are rows no decision reads. Negatives always are.
3. With `SuccessSampling = 1.0`, successes are transmitted.
4. Enqueueing past `FeedbackQueueCapacity` drops rather than blocking, and `DroppedCount` rises.
5. `FlushAsync` splits a queue larger than `FeedbackBatchSize` into multiple calls.
6. `FlushAsync` on an empty queue makes no HTTP call.
7. A transport exception during flush does not propagate to the caller — feedback must never break a scrape.
8. Disposal performs a final flush. (Batch processes die quickly; the last batch holds the events describing the failure that ended the run.)

- [ ] **Step 2: Run to verify failure, then implement**

Requirements: `ConcurrentQueue<FeedbackItem>` plus an `int` count tracked with `Interlocked`; `Enqueue` is non-blocking and allocation-light; flush drains up to `FeedbackBatchSize` per call and loops; all transport exceptions are swallowed and counted.

- [ ] **Step 3: Verify, build, commit**

```bash
dotnet test src/Tests/Proxy.Client.Tests/Proxy.Client.Tests.csproj --filter "FullyQualifiedName~FeedbackBufferTests"
dotnet build src/FS.Proxy.slnx
git add src/Clients/FS.Proxy.Client/Feedback/ src/Tests/Proxy.Client.Tests/Feedback/
git commit -m "feat(client): add the feedback buffer

Enqueue never blocks and the queue is bounded: feedback must not be able
to throttle a scrape, so overflow drops and counts.

Success outcomes are not transmitted by default. The service's
PolicyEvaluationService counts only non-Success events, so sending them
writes rows no decision ever reads; SuccessSampling exists for anyone
who wants the server-side series anyway.

Disposal flushes: these are batch processes, and the last unflushed
batch is exactly the events describing the failure that ended the run.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 8: `ProxySource` — the facade

Ties the pool, the buffer and the cache into the one surface all three adoption levels use. This is the type a legacy 4.8 scraper touches.

**Files:**
- Create: `src/Clients/FS.Proxy.Client/IProxySource.cs`
- Create: `src/Clients/FS.Proxy.Client/ProxySource.cs`
- Test: `src/Tests/Proxy.Client.Tests/ProxySourceTests.cs`

**Interfaces:**
- Produces:
  - `IProxySource` with `IReadOnlyList<ProxyEndpoint> GetProxies(params string[] tags)`, `ProxyEndpoint? Lease(params string[] tags)`, `void Report(Guid proxyId, ProxyOutcome outcome, string? detail = null)`, `Task WarmupAsync(CancellationToken ct = default)`.
  - `ProxySource(ProxyClientOptions options)` implementing it, `IAsyncDisposable`, plus `static IProxySource Instance` and `static void Initialize(ProxyClientOptions)` for consumers with no DI container.

- [ ] **Step 1: Write the failing tests**

Required tests:

1. `GetProxies` returns the warmed pool's contents.
2. **`GetProxies` performs no I/O** — call it with a transport substitute that throws on any call, after warmup, and assert it still returns proxies. This is the deadlock-safety property the 4.8 scrapers depend on.
3. Tag sets are normalized and order-insensitive: `GetProxies("Country:CL", "entityType:Tender")` and `GetProxies("entitytype:tender", "country:cl")` hit the same pool, and the transport is called once.
4. `Report` with a negative outcome quarantines the proxy locally so `Lease` stops returning it — without waiting for the server.
5. `Report` enqueues to the buffer.
6. `Instance` throws a clear error if used before `Initialize`.
7. Disposal flushes feedback and stops the refresh timer.

- [ ] **Step 2: Run to verify failure, then implement**

Requirements:

- One `ProxyPool` per normalized tag set, in a `ConcurrentDictionary` keyed by the sorted, normalized, joined tags.
- A `System.Threading.Timer` per pool drives `RefreshAsync` on the jittered interval; a second timer drives `FlushAsync`. Both are created in `WarmupAsync` and disposed with the source.
- `Report` does two things: quarantine locally **and** enqueue. The local quarantine is immediate self-protection; the enqueued event is what lets the server decide for the whole fleet. Say so in a comment — it is the part most likely to be "simplified" away.
- `Instance` is a plain static with a null check; no lazy double-checked locking, no thread-static.

- [ ] **Step 3: Verify, build, commit**

```bash
dotnet test src/Tests/Proxy.Client.Tests/Proxy.Client.Tests.csproj
dotnet build src/FS.Proxy.slnx
git add src/Clients/FS.Proxy.Client/IProxySource.cs src/Clients/FS.Proxy.Client/ProxySource.cs src/Tests/Proxy.Client.Tests/ProxySourceTests.cs
git commit -m "feat(client): add the ProxySource facade

GetProxies is synchronous and does no I/O — a background timer keeps the
snapshot current. This is not a convenience: the consuming scrapers are
full of .Result, and an async-only surface would deadlock them.

Report both quarantines locally and enqueues. The local quarantine
protects this run immediately; the enqueued event lets the server decide
for the whole fleet. Dropping either one looks like a simplification and
is not.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 9: `DelegatingHandler` and the net10 DI extensions

Adoption level 2 — for code written from here on, not a migration path.

**Files:**
- Create: `src/Clients/FS.Proxy.Client/Http/FsProxyRotationHandler.cs`
- Create: `src/Clients/FS.Proxy.Client/DependencyInjection/ServiceCollectionExtensions.cs`
- Modify: `src/Clients/FS.Proxy.Client/FS.Proxy.Client.csproj` (net10-only package references)
- Test: `src/Tests/Proxy.Client.Tests/Http/FsProxyRotationHandlerTests.cs`

**Interfaces:**
- Produces: `FsProxyRotationHandler(IProxySource source, string[] tags, Func<HttpResponseMessage, ProxyOutcome?>? classifyResponse = null)`; and, behind `#if NET`, `IServiceCollection.AddFsProxyClient(IConfiguration section)` and `IHttpClientBuilder.AddFsProxyRotation(params string[] tags)`.

The DI extensions need `Microsoft.Extensions.DependencyInjection.Abstractions`, `Microsoft.Extensions.Configuration.Binder` and `Microsoft.Extensions.Http`, all `Condition="'$(TargetFramework)' == 'net10.0'"`. Check `src/Directory.Packages.props` for each; add a `PackageVersion` for any that is missing.

- [ ] **Step 1: Write the failing tests**

Required tests, all against a fake `IProxySource` and a stub inner handler:

1. The handler sets the request's proxy and sends.
2. A 200 response reports `Success`.
3. A 403 reports `Banned`.
4. A 503 reports **`Success`** — the proxy worked, the portal did not.
5. A thrown `TaskCanceledException` with a `TimeoutException` inner reports `Timeout` **and rethrows** — the handler reports, it does not swallow.
6. A supplied `classifyResponse` returning `Banned` on a 200 wins over the status code. (This is the captcha-page case; it is the most valuable signal the scrapers have.)
7. With no proxy available, the request is sent directly rather than failing.

**Note on mechanics:** `HttpClientHandler.Proxy` is set per-handler, not per-request, so a `DelegatingHandler` cannot change the proxy of an in-flight request. Implement this by having the handler own a small cache of `HttpMessageInvoker` per proxy endpoint, or by documenting that consumers must use `SocketsHttpHandler` with a per-request `WebProxy` selected through `IWebProxy`. Resolve this in implementation and write the approach into the XML doc — if neither works cleanly, report DONE_WITH_CONCERNS rather than shipping a handler that silently ignores the proxy.

- [ ] **Step 2: Run to verify failure, then implement**

- [ ] **Step 3: Verify, build, commit**

```bash
dotnet test src/Tests/Proxy.Client.Tests/Proxy.Client.Tests.csproj
dotnet build src/FS.Proxy.slnx
git add src/Clients/FS.Proxy.Client/ src/Tests/Proxy.Client.Tests/
git commit -m "feat(client): add the rotation DelegatingHandler and net10 DI extensions

Adoption level 2 — for new code, not a migration path. Reports the
outcome of every response through the shared classifier, and rethrows
rather than swallowing: the handler observes, it does not change control
flow.

The classifyResponse hook is where a caller recognizes a soft block — a
portal answering 200 with a captcha page, which no generic rule can
detect and which is the most valuable signal these scrapers have.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 10: Pack, publish to the local feed, and write the integration guide

The deliverable phases 3 and 4 consume. Those phases happen in the TAG and legacy repositories, not here — what ships from this repo is a package and a guide.

**Files:**
- Modify: `src/Clients/FS.Proxy.Client/FS.Proxy.Client.csproj` (final package metadata)
- Create: `docs/integration/fs-proxy-client.md`
- Modify: `.gitignore` (the snapshot cache file pattern)

- [ ] **Step 1: Pack**

```bash
dotnet pack src/Clients/FS.Proxy.Client/FS.Proxy.Client.csproj -c Release
```

Expected: one `FS.Proxy.Client.0.1.0-preview.1.nupkg`, **no** `.snupkg` (the csproj sets `IncludeSymbols=false`; `Directory.Build.targets` would otherwise turn it on for every packable project, and a folder feed cannot serve symbol packages).

- [ ] **Step 2: Verify the package contains both target frameworks**

```bash
unzip -l src/Clients/FS.Proxy.Client/bin/Release/*.nupkg | grep -E "lib/"
```

Expected: entries under **both** `lib/netstandard2.0/` and `lib/net10.0/`. Only one means the multi-target silently collapsed — stop and fix before publishing.

- [ ] **Step 3: Push to the local feed**

`NuGet.config` already declares `fsh-local` pointing at a folder.

```bash
FEED=$(grep -o 'key="fsh-local" value="[^"]*"' NuGet.config | sed 's/.*value="//;s/"//')
echo "feed: $FEED"
dotnet nuget push src/Clients/FS.Proxy.Client/bin/Release/FS.Proxy.Client.0.1.0-preview.1.nupkg --source "$FEED"
ls "$FEED" | grep FS.Proxy.Client
```

- [ ] **Step 4: Add the cache file pattern to `.gitignore`**

The snapshot cache holds proxy credentials in plaintext by design. It belongs in a runtime directory, but a defence in depth costs one line:

```
# FS.Proxy.Client snapshot cache — proxy credentials in plaintext, runtime artifact
*.fsproxy-snapshot.json
```

- [ ] **Step 5: Write the integration guide**

`docs/integration/fs-proxy-client.md`. It must let someone integrate without reading the SDK source, and it is the input to phases 3 and 4, which happen in other repositories. Cover:

- **Install:** the package id, the version, and that the consuming repo needs the `fsh-local` feed in its own `NuGet.config`. State plainly that the configured feed path is a developer home directory and therefore serves one machine — a network share or a hosted feed is a prerequisite before another repo can consume this.
- **Configuration:** the full `ProxyClientOptions` surface with defaults, and that `ApiKey` comes from an environment variable, never a config file.
- **Level 0, legacy 4.8 on `WebRequest`:** the `ProxySource.Initialize` + `GetProxies` + `ToWebProxy()` + `Report` loop, as complete compilable code.
- **Level 1, TAG's `WebScraper`:** written against the real file, which was read for this plan at
  `/Users/eduardo/dev/falconsoft/tenderactivegrabber/FST.TAG/src/Core/Application.Common/Common/Scraping/WebScraper/`
  (TAG is on `develop`, net10.0). Four concrete changes, and the guide must state all four because
  three of them are not obvious from the spec:

  1. **`LoadWebScraperConfiguration()` (WebScraper.cs:658-673)** — `Proxies = _settings.Proxies;`
     becomes a projection over `IProxySource.GetProxies(tags)`. This is the one change the spec
     already describes.
  2. **`ProxyInfo` gains `Guid ProxyId`** (`WebScraperSettings.cs`). Its existing `Id` is an `int`
     index from the JSON file and cannot address a proxy on the service.
  3. **`GetProxy()` (WebScraper.cs:212-248) must retain the selected proxy.** It currently sets
     `ProxyKey` but keeps no reference to the `ProxyInfo` it chose, so at the moment of failure
     there is nothing to report against. Add a `_currentProxy` field set alongside `ProxyKey`.
  4. **`RenewProxy()` (WebScraper.cs:269-273) has to take the outcome.** It is parameterless today
     and is called from **fourteen** `catch` blocks (WebScraper.cs lines 195, 207, 745, 757, 840,
     884, 896, 960, 1013, 1025, 1071, 1083, 1112, 1124). Change the signature to
     `RenewProxy(ProxyOutcome outcome, string detail)` and update all fourteen. The mapping is
     mechanical because the call sites already split by exception family: the
     `catch (HttpRequestException ex)` sites pass `ProxyOutcomeClassifier.FromException(ex)`, and
     the `catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)` sites pass
     `ProxyOutcome.Timeout`.

     The guide must say why the cheaper alternative is wrong: leaving `RenewProxy()` parameterless
     and reporting a blanket `Failure` would erase the Timeout/Banned distinction, which is the
     entire signal the policy engine acts on.

  Also flag, without prescribing a fix, that **`PinProxy()` (WebScraper.cs:258-267) replaces the
  `Proxies` list wholesale**. A client pinned that way stops receiving pool refreshes — acceptable
  for its current caller (attachment sessions choosing by local reputation), but it must be a
  conscious choice in phase 3, not a surprise.
- **Level 2, new code:** `AddFsProxyClient` + `AddFsProxyRotation`.
- **The tag taxonomy:** the five categories, that an attachment scrape is `entitytype:tender` + `operationtype:attachments` combined, and that custom tags are plain strings.
- **What to expect when things break:** stale-snapshot serving, the stale ceiling, local quarantine, and that `Success` is not transmitted by default.
- **What is NOT in this release:** the Redis L2 cache (deferred), idempotency on the batch endpoint, and that a long batch behind one request timeout plus a blind retry can duplicate events — so **the retry policy in phase 3 must not be blind**. Cross-reference the spec's follow-ups.

- [ ] **Step 6: Full verification**

```bash
dotnet build src/FS.Proxy.slnx
dotnet test src/FS.Proxy.slnx
```

Expected: all green, 0 warnings.

- [ ] **Step 7: Commit**

```bash
git add src/Clients/FS.Proxy.Client/FS.Proxy.Client.csproj docs/integration/ .gitignore
git commit -m "feat(client): pack FS.Proxy.Client and document integration

Publishes 0.1.0-preview.1 to the fsh-local folder feed and adds the
integration guide phases 3 and 4 consume — those phases are work in the
TAG and legacy scraper repositories, not this one.

Notes in the guide that the configured feed path is a developer home
directory and serves one machine: a network share or hosted feed is a
prerequisite before another repo can consume this package.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Out of scope for this plan

- **Phases 3 and 4** — integrating TAG and the legacy scrapers. Separate repositories; this plan ships the package and the guide they consume.
- **`FS.Proxy.Client.Redis`** — the L2 cache. Deferred by decision on 2026-09-15; `IProxySnapshotCache` is the seam that lets it land later without a breaking change.
- **Idempotency on `/feedback/batch`** — spec §4 decision 4.
- **Long-batch timeout amplification** — spec follow-ups. The guide warns phase 3 against a blind retry; the server-side mitigation is a separate decision.
- **`ProxyUsageEvents` retention** — spec follow-ups.
- **Migrating the attachment reputation store** — spec §6 keeps it in TAG deliberately.
