# HTTP/3 Research: 0-RTT, QUIC, WebTransport, ALPS Evaluation

Research date: February 2026
Applicable BifrostQL targets: net8.0, net9.0, net10.0

## Executive Summary

BifrostQL already ships `BifrostHttp3Extensions` that configures Kestrel for HTTP/1.1 + HTTP/2 + HTTP/3
protocol negotiation. This document evaluates five advanced HTTP/3 features for deeper integration.

**Recommendation**: No additional implementation needed at this time. The existing HTTP/3 configuration
already enables the highest-value features (independent QUIC streams, protocol auto-negotiation).
The remaining features are either not yet available in .NET, carry security trade-offs that outweigh
the benefits for a database-proxying API, or require no application-level code.

---

## 1. 0-RTT Connection Establishment

### What It Is

TLS 1.3 allows clients that have previously connected to a server to resume the session with
zero round trips (0-RTT). The client sends encrypted application data alongside the TLS
ClientHello, eliminating the handshake latency on reconnection.

In HTTP/3, this combines with QUIC's own 0-RTT mechanism: QUIC stores a transport-level
session ticket, and TLS 1.3 stores a PSK (pre-shared key). Together, they allow the first
flight to carry both the QUIC handshake and an HTTP request.

### .NET Support Status

| Framework | Kestrel HTTP/3 | TLS 1.3 | 0-RTT (Early Data) |
|-----------|---------------|---------|---------------------|
| net8.0    | GA            | GA      | Not exposed         |
| net9.0    | GA            | GA      | Not exposed         |
| net10.0   | GA            | GA      | Not exposed         |

**Kestrel does not expose configuration to enable or disable 0-RTT (TLS early data).**

The underlying `SslStream` and `SslServerAuthenticationOptions` in .NET do not surface an
`AllowEarlyData` property. The QUIC implementation (`System.Net.Quic`, backed by msquic)
handles session tickets internally, but the TLS early data accept/reject decision is not
exposed to application code.

On Linux, msquic links against OpenSSL, which supports TLS 1.3 0-RTT natively. On Windows,
msquic uses Schannel. In both cases, the session resumption (1-RTT with cached tickets) works
automatically, but the true 0-RTT early data path is not surfaced.

### Replay Attack Considerations

0-RTT data is inherently vulnerable to replay attacks because it is sent before the handshake
completes. An attacker who captures the initial flight can replay it to the server. This is
acceptable for idempotent operations (GET requests) but dangerous for mutations.

BifrostQL serves mutations (INSERT, UPDATE, DELETE) over POST requests. Enabling 0-RTT for
mutation traffic would require:

1. Distinguishing idempotent queries from mutations at the TLS layer (impossible without
   application-layer inspection).
2. Implementing a server-side replay cache keyed by client ticket + request hash.
3. Limiting 0-RTT to reads only, which requires splitting transport paths.

None of these are practical for a general-purpose GraphQL API server.

### BifrostQL Applicability

**Low.** BifrostQL is a server-side library, not a client. The 0-RTT benefit accrues primarily
to clients reconnecting after brief disconnections (mobile apps, SPAs with connection pooling).
On the server side, Kestrel handles TLS negotiation automatically. There is nothing BifrostQL
needs to configure beyond what `UseBifrostHttp3` already does.

### Action Items

- **Defer.** No action until .NET exposes 0-RTT configuration in Kestrel/QUIC.
- **Monitor** `dotnet/runtime` issues for `QuicConnection.AllowEarlyData` or similar API.
- **If/when available**: Only enable for safe (query-only) endpoints. Never for mutations
  without a replay mitigation layer.

---

## 2. Independent QUIC Streams (Head-of-Line Blocking Elimination)

### What It Is

HTTP/2 multiplexes multiple logical streams over a single TCP connection. Because TCP guarantees
ordered, reliable delivery, a single lost packet blocks all streams on that connection until
retransmission succeeds. This is TCP head-of-line (HOL) blocking.

HTTP/3 replaces TCP with QUIC. Each HTTP/3 stream maps to an independent QUIC stream with its
own flow control and loss recovery. A lost packet on stream A does not block streams B, C, or D.
This is the single largest practical improvement of HTTP/3 over HTTP/2.

### .NET Support Status

| Framework | QUIC Independent Streams | Status |
|-----------|-------------------------|--------|
| net8.0    | Yes (via msquic)        | GA     |
| net9.0    | Yes (via msquic)        | GA     |
| net10.0   | Yes (via msquic)        | GA     |

**Fully supported and enabled automatically when Kestrel uses HTTP/3.**

When `BifrostHttp3Extensions.ConfigureKestrelForHttp3` sets
`listenOptions.Protocols = HttpProtocols.Http1AndHttp2AndHttp3`, clients that negotiate HTTP/3
automatically get independent QUIC streams. No additional configuration is needed.

### How HOL Blocking Elimination Helps BifrostQL

BifrostQL commonly serves multiple concurrent GraphQL requests from a single client (batched
queries, parallel data fetches from a React frontend). Under HTTP/2:

- Client sends 5 GraphQL queries on streams 1-5 over a single TCP connection.
- Server responds to streams 2-5 quickly, but stream 1 has a slow database query.
- A single TCP packet loss on stream 1's response blocks delivery of streams 2-5.

Under HTTP/3:

- Same 5 queries on 5 QUIC streams.
- Packet loss on stream 1 only affects stream 1. Streams 2-5 deliver immediately.

This is particularly valuable for the `__join` pattern where a client issues nested queries
that vary widely in execution time.

### BifrostQL Applicability

**High, and already implemented.** The existing `BifrostHttp3Extensions` enables this. No
additional work is needed. Clients connecting via HTTP/3 automatically benefit.

### Action Items

- **Done.** Already implemented via `UseBifrostHttp3`.
- **Documentation**: Consider adding a note in the README that BifrostQL supports HTTP/3
  with automatic HOL blocking elimination for concurrent queries.

---

## 3. Connection Migration

### What It Is

QUIC connections are identified by a Connection ID, not by the 4-tuple (source IP, source port,
destination IP, destination port) as in TCP. When a client changes IP address (e.g., switching
from WiFi to cellular), the QUIC connection survives because both endpoints match on the
Connection ID, not the network address.

This eliminates the reconnection cost when mobile clients roam between networks.

### .NET Support Status

| Framework | QUIC Connection Migration | Status |
|-----------|--------------------------|--------|
| net8.0    | Passive only             | Partial |
| net9.0    | Passive only             | Partial |
| net10.0   | Passive only             | Partial |

**Explanation of "passive only":** msquic supports *passive* connection migration, where the
server accepts packets from a new client address after validating the Connection ID. The server
does not initiate migration (active migration is a client-side concern). This is sufficient
for the server-side use case.

However, .NET does not expose connection migration events to application code. There is no
callback like `OnConnectionMigrated` for logging or security auditing. The migration happens
transparently in the QUIC layer.

### Testing on Mobile Networks

Testing connection migration requires:

1. A client that supports HTTP/3 (e.g., curl with HTTP/3, a browser, or a .NET `HttpClient`
   configured for HTTP/3).
2. A network environment where the client IP changes during an active request (mobile network
   handoff, VPN reconnect, or simulated with network namespaces).
3. Validation that the HTTP/3 connection ID persists across the IP change and the response
   completes without error.

Automated testing is possible using Linux network namespaces to simulate IP changes:

```bash
# Create a network namespace, start a request, move the client to a new namespace mid-request
ip netns add test_migration
# ... (complex setup involving veth pairs and iptables)
```

This is infrastructure-level testing, not application-level.

### BifrostQL Applicability

**Medium, and already enabled passively.** Connection migration is handled entirely by the
QUIC transport layer (msquic). BifrostQL does not need to do anything to support it. Clients
using HTTP/3 get connection migration for free.

The BifrostQL binary WebSocket transport (`BifrostBinaryMiddleware`) would NOT benefit from
QUIC connection migration because WebSocket connections run over TCP (HTTP/1.1 Upgrade) or
HTTP/2. If WebSocket traffic migrated to HTTP/3's WebSocket-over-QUIC (extended CONNECT), it
would gain connection migration. However, this requires client support that is not yet widely
available.

### Action Items

- **Defer.** No application-level work needed. Connection migration is transparent.
- **Monitor** msquic and .NET for connection migration event APIs if audit logging is desired.
- **Future**: When WebSocket-over-HTTP/3 matures, evaluate migrating `BifrostBinaryMiddleware`
  to use extended CONNECT instead of HTTP/1.1 WebSocket upgrade.

---

## 4. WebTransport

### What It Is

WebTransport is a web API for bidirectional communication between a client and server over
HTTP/3. Unlike WebSocket (which uses a single ordered byte stream), WebTransport provides:

- Multiple independent bidirectional streams (via QUIC streams).
- Unidirectional streams (for server push or client upload).
- Datagrams (unreliable, unordered, low-latency messages).

WebTransport runs over HTTP/3 using the Extended CONNECT protocol (RFC 9220), which means it
benefits from all QUIC properties: 0-RTT, connection migration, independent streams, and
HOL blocking elimination.

### .NET Support Status

| Framework | WebTransport Server | WebTransport Client | Status |
|-----------|--------------------|--------------------|--------|
| net8.0    | Experimental       | No                 | Preview |
| net9.0    | Experimental       | No                 | Preview |
| net10.0   | Partial            | No                 | Limited |

**Kestrel WebTransport status:**

- .NET 7 introduced experimental WebTransport support behind a feature flag.
- .NET 8-9 carried it forward but it remained experimental and not production-ready.
- .NET 10 has improved the API surface but it is still not GA.

The API surface in Kestrel:

```csharp
// Accepting a WebTransport session (experimental)
app.Run(async context =>
{
    var feature = context.Features.Get<IHttpWebTransportFeature>();
    if (feature is not null && feature.IsWebTransportRequest)
    {
        var session = await feature.AcceptAsync(CancellationToken.None);
        // session.OpenUnidirectionalStreamAsync()
        // session.AcceptStreamAsync()
        // session.AcceptDatagramAsync() -- not yet stable
    }
});
```

**Key limitations:**

1. No stable API contract -- the interface may change between .NET versions.
2. Client-side .NET support (HttpClient with WebTransport) does not exist.
3. Browser support: Chrome has WebTransport GA since 97. Firefox and Safari have partial
   support. Edge follows Chrome.
4. No production deployment guidance from Microsoft.

### BifrostQL Applicability

**Medium-term interest, defer implementation.**

BifrostQL already has a binary transport layer (`BifrostBinaryMiddleware`) over WebSocket
with chunking, CRC32 integrity, backpressure (ACK windowing), and retry/resume. WebTransport
would be a natural evolution because:

1. **Multiple independent streams** map well to BifrostQL's multiplexed request model
   (request_id-based multiplexing over a single WebSocket would become true QUIC stream
   multiplexing with independent flow control).
2. **Datagrams** could serve low-latency notifications (schema change events, subscription
   pings) without head-of-line blocking.
3. **Connection migration** would carry over from QUIC.

However, the existing WebSocket transport works reliably and has broad client support.
WebTransport's client ecosystem is not mature enough to justify a parallel implementation.

### Comparison: Current Binary Transport vs. WebTransport

| Feature | WebSocket (current) | WebTransport (future) |
|---------|--------------------|-----------------------|
| Multiplexing | Application-level (request_id) | Native QUIC streams |
| HOL blocking | Yes (TCP) | No (QUIC) |
| Backpressure | ACK windowing (application) | Per-stream flow control (QUIC) |
| Integrity | CRC32 per chunk | QUIC packet-level CRC |
| Retry/Resume | ChunkBuffer + Resume messages | QUIC loss recovery (automatic) |
| Connection migration | No | Yes (QUIC) |
| Client support | Universal | Chrome, limited Firefox/Safari |
| Server support | GA (.NET 8+) | Experimental (.NET 8-10) |

### Action Items

- **Defer.** Do not implement until Kestrel WebTransport reaches GA.
- **Architecture note**: When implementing, WebTransport would replace (not layer on top of)
  `BifrostBinaryMiddleware`. The chunking, ACK windowing, and resume logic become unnecessary
  because QUIC handles all of this natively. The `BifrostMessage` protobuf envelope would
  remain, but transport concerns would be removed.
- **Monitor** `dotnet/aspnetcore` for WebTransport GA timeline.
- **Client SDKs**: WebTransport client support in browsers is ahead of .NET. A JavaScript
  client SDK for BifrostQL could adopt WebTransport before a .NET-to-.NET path exists.

---

## 5. ALPS Negotiation (Application-Layer Protocol Settings)

### What It Is

ALPS (Application-Layer Protocol Settings) is a TLS extension (RFC draft) that allows the
server to send application-layer settings to the client during the TLS handshake, before
any application data is exchanged. For HTTP/2 and HTTP/3, this means the server's SETTINGS
frame can be delivered as part of the TLS handshake itself, eliminating one round trip.

In practice, ALPS allows:

- Server to advertise HTTP/2 SETTINGS (max concurrent streams, initial window size, etc.)
  during TLS negotiation.
- Client to learn server capabilities before sending the first HTTP request.
- Potential for preload hints and other early metadata.

### .NET Support Status

| Framework | ALPS Support | Status |
|-----------|-------------|--------|
| net8.0    | No          | Not implemented |
| net9.0    | No          | Not implemented |
| net10.0   | No          | Not implemented |

**ALPS is not implemented in Kestrel, SslStream, or msquic in any current .NET version.**

The ALPS specification itself has limited adoption:

- **Chrome** supports ALPS for HTTP/2 (used for ACCEPT_CH frames to negotiate Client Hints).
- **Chromium-based browsers** (Edge, Brave) inherit Chrome's support.
- **Firefox** does not support ALPS.
- **Safari** does not support ALPS.
- **OpenSSL** added ALPS support in a custom patch (used by BoringSSL/Google's fork).
  Mainline OpenSSL does not include ALPS.
- **BoringSSL** (Google's OpenSSL fork) supports ALPS. Since msquic on Linux uses OpenSSL
  (not BoringSSL), ALPS is not available to .NET on Linux.

### BifrostQL Applicability

**None at this time.**

ALPS provides marginal latency improvement (one fewer round trip for SETTINGS delivery). For
BifrostQL's use case:

1. GraphQL clients typically establish long-lived connections with connection pooling. The
   one-time SETTINGS round trip is amortized across hundreds or thousands of requests.
2. BifrostQL does not use HTTP/2 SETTINGS for application-specific negotiation. The default
   Kestrel SETTINGS (max concurrent streams, etc.) are adequate.
3. The ALPS extension itself is not on a standards track at IETF -- it remains a Google-driven
   draft with limited ecosystem adoption.

### Action Items

- **Skip.** No implementation value for BifrostQL.
- **No monitoring needed** unless ALPS reaches IETF RFC status and .NET adds support, at
  which point it would be a Kestrel-level concern, not an application-level one.

---

## Summary Matrix

| Feature | .NET Support | BifrostQL Value | Recommendation | Priority |
|---------|-------------|-----------------|----------------|----------|
| 0-RTT | Not exposed | Low (mutation replay risk) | Defer | -- |
| Independent QUIC streams | GA | High | **Already implemented** | Done |
| Connection migration | Passive (transparent) | Medium | No action needed | Done |
| WebTransport | Experimental | Medium (future WS replacement) | Defer until GA | Low |
| ALPS | Not implemented | None | Skip | -- |

## Recommendations

### Implement Now

Nothing. The existing `BifrostHttp3Extensions` already enables the highest-value HTTP/3
features (independent streams, passive connection migration, protocol auto-negotiation).

### Defer

1. **WebTransport**: Monitor Kestrel WebTransport GA status. When it ships as stable, evaluate
   replacing `BifrostBinaryMiddleware` WebSocket transport with WebTransport. This would
   eliminate the application-level chunking, ACK windowing, and resume logic.

2. **0-RTT**: Monitor .NET QUIC API for early data configuration. If exposed, only enable
   for query endpoints, never for mutations.

### Skip

1. **ALPS**: No ecosystem support, no application-level benefit, not on standards track.

### No Action Needed

1. **Independent QUIC streams**: Already working via `UseBifrostHttp3`.
2. **Connection migration**: Already working transparently via msquic.

## Deployment Notes

For HTTP/3 to work in production, the deployment environment must support:

1. **UDP port access**: HTTP/3 runs over QUIC, which uses UDP. Firewalls and load balancers
   must allow UDP traffic on the HTTPS port (typically 443).
2. **Alt-Svc header**: Kestrel automatically advertises HTTP/3 availability via the `Alt-Svc`
   response header on HTTP/2 responses. Reverse proxies (nginx, HAProxy) must pass this
   header through or add their own.
3. **TLS certificate**: Required for HTTP/3. Development certificates work for local testing.
4. **msquic availability**: On Linux, the `libmsquic` package must be installed. On Windows,
   msquic is included in the OS (Windows 11+, Windows Server 2022+).
5. **Load balancer support**: AWS ALB does not support HTTP/3. AWS NLB with UDP listeners can
   pass through QUIC. Cloudflare, Azure Front Door, and Google Cloud Load Balancer support
   HTTP/3 natively.
