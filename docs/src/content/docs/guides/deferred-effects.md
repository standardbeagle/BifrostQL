---
title: Deferred effects
---

# Deferred effects

## Decision record: apply then reverse

Deferred changes use **apply-then-reverse**. The original mutation applies through the
normal mutation pipeline immediately, while a durable `change_set` and its
`change_set_delta` reverse data are stored atomically with it. An undo applies the named
inverse operation from the stored pre-image, subject to the undo window and concurrency
checks.

We rejected stage-then-apply: it would require a parallel pending-state read model and
would make normal reads diverge from the database's committed state. Apply-then-reverse
keeps the existing write, audit, authorization, and history semantics authoritative.

Future capture and undo slices must implement this named decision; this guide deliberately
adds no write-path behavior.

## Metadata contract

A reversible table declares all required metadata:

```text
dbo.orders {
  deferrable: enabled
  undo-window: 90d
  hold-events: enabled
  concurrency-token: version
  history: enabled
}
```

`undo-window` accepts a positive integer followed by `d` (days) or `h` (hours), for
example `90d` or `12h`. There is no default window. `hold-events` is optional; when set,
it must be `enabled` and instructs later event-delivery slices to hold outbound events
until finalization.

`deferrable` requires both `concurrency-token` and `history`; model loading rejects a
partial configuration.

## Durable-store contract

The `change_set` store uses the constants in `MetadataKeys.Deferred.ChangeSet.Column`:
`id`, `state`, `undo_window_expires_at`, `requester`, `created_at`, `applied_at`, and
`reversed_at`.

The `change_set_delta` reverse store uses
`MetadataKeys.Deferred.ChangeSetDelta.Column`: `id`, `change_set_id`, `table`, `pk`,
`op`, `inverse_op`, `before_image`, `after_image`, and `created_at`. Primary keys and
images are JSON so composite keys and full inverse data remain representable.
