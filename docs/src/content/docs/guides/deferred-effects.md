---
title: "Deferred and Reversible Change Sets"
description: "Capture a reverse delta for every write, review pending change sets, and undo a committed change through GraphQL while its CDC events stay held until release."
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

Future capture and undo slices must implement this named decision; this guide deliberately
adds no write-path behavior.

Bulk deferred-effects controls in the edit-db UI remain a follow-up; this guide documents
server behavior only and does not add a bulk UI workflow.

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

The `change_set` store uses the constants in `MetadataKeys.Deferred.ChangeSet.Column`:
`id`, `state`, `undo_window_expires_at`, `requester`, `created_at`, `applied_at`, and
`reversed_at`.

The `change_set_delta` reverse store uses
`MetadataKeys.Deferred.ChangeSetDelta.Column`: `id`, `change_set_id`, `table`, `pk`,
`op`, `inverse_op`, `before_image`, `after_image`, and `created_at`. Primary keys and
images are JSON so composite keys and full inverse data remain representable.
