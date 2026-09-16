# FS.Proxy.Client

Client SDK for the **FS Proxy** management service: pooled proxy leasing with outcome feedback, for
.NET Framework 4.8 (`netstandard2.0`) and .NET 10 scrapers.

This package is internal to the `fs-proxy` fork it ships from. It is not affiliated with, and is not
published by, the fullstackhero .NET Starter Kit project that fork was originally generated from.

## Where to start

- **Full integration guide** (install, all three adoption levels, configuration, troubleshooting —
  you should be able to do a whole integration from this one document):
  [`docs/integration/fs-proxy-client.md`](https://github.com/eduardojimenez-fst/fs-proxy/blob/main/docs/integration/fs-proxy-client.md)
  in the `fs-proxy` repository.
- **Design background**, if you want the "why" behind a decision the guide only states:
  [`docs/superpowers/specs/2026-09-14-proxy-client-sdk-design.md`](https://github.com/eduardojimenez-fst/fs-proxy/blob/main/docs/superpowers/specs/2026-09-14-proxy-client-sdk-design.md)
  in the same repository.

## In one line

`IProxySource.GetProxies(tags)` hands back a synchronous, I/O-free snapshot of currently-healthy
proxies for a tag set; `Report(proxyId, outcome)` feeds the outcome of each attempt back so the
service's policy engine can disable proxies that are actually bad, without punishing a healthy proxy
for a destination site's own bad afternoon.
