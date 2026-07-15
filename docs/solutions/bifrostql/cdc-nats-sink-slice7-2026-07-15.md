---
written_at: 2026-07-15T00:00:00Z
source_event: task:01KXEBD1FD1D823YH7DXQP3WQX
module: bifrostql
category: correctness
confidence: high
sources:
  - task:01KXEBD1FD1D823YH7DXQP3WQX
  - git:2d9bbad
  - git:def64c8
tags: [cdc, nats-sink, event-sink, dependency-injection, tryadd, fail-open, review-caught-blocker]
status: steering
recurrence: 1
---

# CDC NATS sink (slice 7): two `TryAddSingleton` do not compose into a fallback

**Lesson.** The correctness review gate blocked a real fail-open defect that
tests missed: registering two opt-in `IEventSink` implementations as two
`services.TryAddSingleton<IEventSink>(...)` calls and relying on a `null`
factory return to "fall through" to the second. It does not fall through — a
webhook-only host silently ran with no sink, regressing the slice-5 webhook
delivery path. Pin the mechanism so the next multi-implementation opt-in seam
is a single runtime selection, not stacked TryAdds.

## `TryAdd` keys on descriptor presence at registration time, not the factory's runtime return

The seam wired NATS first, webhook second:

```csharp
services.TryAddSingleton<IEventSink>(sp => natsUrlBlank ? null! : BuildNats(sp));   // registered
services.TryAddSingleton<IEventSink>(sp => webhookUrlBlank ? null! : BuildWebhook(sp)); // NO-OP
```

`TryAddSingleton` is a no-op when a descriptor for the service type **already
exists in the collection** — that check happens at *registration* time, before
any factory runs. So the second call sees the NATS descriptor already present
and never registers. At resolution, `GetService<IEventSink>()` runs only the
NATS factory; when the NATS URL is blank it returns `null`, and the webhook
factory — the one that would have delivered — was never in the container to
begin with. The "fall through on null return" the comment claimed cannot
happen: a runtime `null` return from the winning factory does not resurrect a
registration that was never made. Result: **webhook-only host ⇒ null sink ⇒
zero deliveries**, a fail-open regression of a shipped path.

## Fix: one descriptor, one runtime selection, tested at the seam

Collapse to a single `TryAddSingleton<IEventSink>` whose factory makes the
choice explicitly, and extract the choice into a pure, dependency-free unit so
it is testable without a broker or an HTTP client:

```csharp
services.TryAddSingleton<IEventSink>(sp =>
    CdcSinkSelector.Select(
        natsUrl,   buildNats:    () => BuildNats(sp),
        webhookUrl, buildWebhook: () => BuildWebhook(sp))!); // null ⇒ dispatcher idles
```

`CdcSinkSelector.Select` applies NATS-then-webhook-then-null precedence and
invokes **only the chosen** builder, so the loser opens no connection and
constructs no `HttpClient`. Because the selection lives in Core (not in the
Server DI wiring), its unit test proves all four resolution outcomes
(both⇒NATS, NATS-only⇒NATS, webhook-only⇒webhook, neither⇒null) broker-free
and http-free — and every case asserts the loser's builder was never called.

## General rule for any multi-implementation opt-in registration

When more than one concrete implementation of the same service can be
configured and exactly one must win, do NOT stack `TryAdd*` calls and lean on
factory `null` returns for precedence — the second and later registrations are
dead no-ops. Register a **single** descriptor whose factory selects at runtime,
and put the selection logic somewhere it can be unit-tested independently of
the DI container. A `null`-returning singleton factory is also a latent trap
for any future `GetRequiredService<T>` consumer (it throws), so document that
the sink is intentionally optional and resolved via `GetService<T>`.

## Process note: this was a review-caught blocker, not a test failure

The full suite was green (4828 passed, 0 skipped) with the defect present,
because no test resolved the webhook-only DI configuration. The adversarial
correctness gate caught it by tracing the factory. Multi-implementation opt-in
registration seams need either a resolution test per configuration or a review
checklist item — a green unit suite over the happy path does not exercise the
"which implementation wins" question.
