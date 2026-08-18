---
title: "Deferred and Reversible Change Sets"
description: "Capture a reverse delta for every write, review pending change sets, and undo a committed change through GraphQL while its CDC events stay held until release."
---

Deferred effects make a committed write reversible. A table marked `deferrable` captures a
reverse delta inside the same transaction as every write, so a later `undo` mutation can
replay the inverse through the mutation pipeline. Tables that also declare `hold-events`
park their CDC events until the undo window closes or a reviewer approves the change set.

## Decision record: apply then reverse

Deferred changes use **apply-then-reverse**. The original mutation applies through the
normal mutation pipeline immediately, while a durable `change_sets` row and its
`change_set_deltas` reverse data are stored atomically with it. An undo applies the named
inverse operation from the stored pre-image, subject to the undo window and concurrency
checks.

We rejected stage-then-apply: it would require a parallel pending-state read model and
would make normal reads diverge from the database's committed state. Apply-then-reverse
keeps the existing write, audit, authorization, and history semantics authoritative.

### Compensating review is not maker-checker approval

An `until-approved` event hold adds a review queue to this live-data model; it does not
turn the mutation into maker-checker approval. The requester's database write is already
committed and visible to ordinary reads. Approval releases its parked outbox events.
Rejection runs the stored inverse through the normal mutation pipeline as a compensating
write, so concurrency drift can produce conflicts or a partial reversal.

Held rows are still live: deferred effects add no read filter. If a row is edited again
inside an open undo window, the later change captures a newer concurrency token. Reversals
therefore have LIFO semantics for overlapping rows: undo the newest change first; attempting
an older overlapping undo after a newer reversal is an explicit conflict rather than a silent
overwrite. Timed expiry never releases an `until-approved` hold; only an authorized approval
does, while a rejection suppresses its parked events as part of the compensating undo.

By contrast, maker-checker keeps a proposed write outside the authoritative tables until
an approver accepts it. Its approval applies the proposal for the first time, and its
rejection merely discards pending data. Applications that require unapproved values to
remain invisible must use maker-checker approval rather than the deferred review queue.

The GraphQL review surface is emitted when a table uses `hold-events: until-approved`:

```graphql
query {
  deferredReviewQueue { changeSetId requester tenant tables createdAt }
}

mutation {
  approveDeferredChangeSet(changeSetId: 42)
}
```

`rejectDeferredChangeSet(changeSetId: ID!)` returns `DeferredUndoResult`. Queue entries
are tenant-scoped, require readable policy on every affected table, and are omitted when
configuration or policy evaluation fails. Approval and rejection additionally require
each table's approver role and honor `self-approve: false`.

Bulk deferred-effects controls in the edit-db UI remain a follow-up. This guide documents
server behavior only.

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
example `90d` or `12h`. There is no default window. `hold-events` is optional. `enabled`
parks outbound events until undo-window finalization; `until-approved` parks them until
an authorized reviewer approves the live change or rejects it with a compensating undo.

`deferrable` requires both `concurrency-token` and `history`; model loading rejects a
partial configuration.

## Durable-store contract

Both store tables must exist in the model before any table may declare `deferrable`.
Model loading rejects a missing store table or a missing column.

The `change_sets` store uses the constants in `MetadataKeys.Deferred.ChangeSet.Column`:
`id`, `state`, `undo_window_expires_at`, `requester`, `tenant`, `tables`, `created_at`,
`applied_at`, and `reversed_at`. The `tables` column holds a JSON array of the
`schema.name` values the change set touched.

The `change_set_deltas` reverse store uses
`MetadataKeys.Deferred.ChangeSetDelta.Column`: `id`, `change_set_id`, `table`, `pk`,
`op`, `inverse_op`, `before_image`, `after_image`, and `created_at`. Primary keys and
images are JSON so composite keys and full inverse data remain representable.

## Capture

`DeferredDeltaMutationHook` implements `IInTransactionMutationHook` and runs inside the
mutation transaction, so a change set and its deltas commit atomically with the write they
describe. The hook is registered unconditionally and returns immediately for a table that
is not `deferrable`, so a deployment with no reversible table pays nothing.

One change set covers one mutation transaction. A batch or a nested TreeSync write records
several deltas against a single `change_sets` row, and every table it touches is appended
to the `tables` column.

Each delta records the reverse of the write:

| `op` | `inverse_op` | Inverse data |
|---|---|---|
| `insert` | `delete` | `after_image` |
| `update` | `restore` | `before_image` |
| `delete` | `restore` | `before_image` |

The before-image comes from the history hook, which is why `deferrable` requires
`history: enabled` over all columns. For a write that leaves a row in place, the hook
re-reads the stored row to record a true `after_image`, so defaults, triggers, and
generated concurrency tokens are part of the undo contract. A write whose before-image is
missing throws rather than storing a delta it cannot reverse.

## Undo

`undo` is emitted whenever any table is `deferrable`:

```graphql
mutation {
  undo(changeSetId: 42) {
    changeSetId
    undoneRows
    conflictRows
    alreadyUndone
  }
}
```

Undo reverses a whole change set. Each delta routes its inverse through
`IMutationIntentExecutor`, so tenant scoping, policy, soft delete, audit columns, and
field encryption apply to the reversal exactly as they applied to the original write.
The engine builds no predicate of its own.

The captured concurrency token travels with each inverse. A row that changed since capture
fails its optimistic-concurrency check and counts in `conflictRows` instead of being
overwritten. This is the mechanism behind the LIFO rule above: reverse the newest change
first, and an older overlapping undo reports a conflict.

A change set moves through `held` while its window is open, `undoing` while a reversal is
in flight, and then `undone` when every delta reversed or `partial` when any conflicted.
Undoing an already-undone set returns `alreadyUndone: true` and changes nothing, so a
retried client request is safe. An interrupted undo resumes: the engine probes each delta
and skips inverses already applied. A change set whose window expired can no longer be
undone, while a resumed `undoing` set and a rejected approval hold both proceed.

## Held CDC events and their release

A table that declares `hold-events` writes its outbox rows with `state: pending_hold`.
The CDC dispatcher only picks up rows in `pending` or with no state, so held events stay
invisible to subscribers until the change set settles.

- **Timed release.** `DeferredOutboxReleaseHostedService` polls every five seconds, flips
  each `held` change set whose undo window has passed to `released`, and moves its outbox
  rows from `pending_hold` to `pending`. Change sets held `until-approved` are excluded.
- **Approved release.** `approveDeferredChangeSet` releases one change set through the same
  path.
- **Suppression on undo.** Undoing a change set moves its undispatched rows to
  `suppressed`. Events that already went out cannot be recalled, so the engine inserts one
  `compensate` event per dispatched row, in the same transaction and at most once, letting
  subscribers unwind what they acted on.

The release service resolves its model per pass and logs and retries on failure, so a bad
pass never stops the host.

## Related

- [Emitting Change Events from Tables](/BifrostQL/guides/cdc-events/) — configuring the
  outbox these holds park events in.
- [Maker-Checker Approval for Row Edits](/BifrostQL/guides/approval-workflows/) — the
  model to use when unapproved values must stay invisible.
- [Recording Row Change History](/BifrostQL/guides/change-history/) — the before-image
  trail capture depends on.
