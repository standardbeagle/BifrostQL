---
written_at: 2026-07-22T19:30:00Z
source_event: task:01KY4Q2DSR66NJHT4XVQM5ERJZ
module: bifrostql
category: test-quality
confidence: high
form: constraint
sources:
  - task:01KY4Q2DSR66NJHT4XVQM5ERJZ#review-attempt-1
  - task:01KY4Q2DSR66NJHT4XVQM5ERJZ#review-attempt-2
  - git:c1deda8
  - git:58b9b09
tags: [rewind, test-quality, vacuous-fixture, revert-prove-red, rfc-conformance, deterministic-output, feeds, protocol-adapter]
status: steering
recurrence: 1
---

# RSS feeds slice-2 harvest: vacuous guards, RFC required-fields, deterministic output

Two rewinds on one slice (RFC atom:author gap, then a vacuous re-expansion
guard). Three durable lessons.

## Lesson 1 (promoted) — a regression test added with a fix must be revert-proven RED

**Lesson:** A regression test that passes against the pre-fix (buggy) code is
vacuous — it reads as coverage and guards nothing. Prove RED by reverting the
fix before trusting the guard.

**What didn't work:** Attempt 2's single-placeholder fixture (`"Post: {title}"`)
for the template re-expansion fix. The bug (sequential `String.Replace`
re-expanding a placeholder token injected by a row value) only manifests with
>=2 placeholders where an earlier substitution injects a later placeholder's
token. One placeholder = one pass = no divergence. The reviewer reverted fix
84479ed and the test still passed. Attempt 3 fixed it with `"{title} - {slug}"`,
`title="evil {slug}"`, `slug="leaked-slug"`: single-pass yields the inert
`"evil {slug} - leaked-slug"`, sequential-Replace leaks `"evil leaked-slug -
leaked-slug"`. Both implementer and reviewer independently revert-proved RED.

**Why it recurs:** Shared fixtures are tuned for the common case (single
placeholder, single-column PK, `id=1`, single data source, no pre-existing
target state). A multi-element bug cannot be exercised by them. Third occurrence
of the fixture-too-simple class (S3 slice-1 key-addressed writes, CDC slice-4b
single-source fixtures, now feeds). **Promoted to `.claude/rules/regression-test-non-vacuous.md`.**

**Apply when:** any commit that closes a bug by adding/changing a test.
**Prevention:** implementer revert-proves RED and reports the proof in the commit
message; reviewer repeats the experiment for any "add a regression test" rework.
Build the test fixture from the bug's minimal reproduction, not the default
fixture; parameterize the shared builder (default preserves prior behavior)
rather than mutating it.

## Lesson 2 — a standards format's "required fields" come from the spec's conformance clauses, not the element's local field list

**Lesson:** For any adapter emitting a standards-defined wire format
(Atom/RSS/OData/S3 XML/pg catalog), derive required fields from the spec's
conformance MUSTs, not from the element's obvious child list. A test asserting
the implementer's own field list is circular evidence of completeness.

**What didn't work:** AtomFeedWriter omitted `atom:author`, and the writer test
pinned the same reduced field set — encoding the omission. `atom:author` is
required by a *conditional* MUST in RFC 4287 sec 4.1.1 (one or more on
`atom:feed` unless every entry carries its own) that does not appear in the
`atom:feed` child list. Structural validity (`XDocument.Parse` succeeds) is not
spec conformance.

**Apply when:** slicing any standards-wire-format writer.
**Prevention:** put a spec-conformance required-fields checklist in the slice
acceptance criteria. Encode a mandatory field as a `required` type member
(FeedOptions.Author / FeedDocument.Author) so an authorless document is a
compile error at every construction site — fail-safe by construction, not a
runtime validation gap.

## Lesson 3 — cacheable/scraped generated documents must have deterministic output for identical input

**Lesson:** Any generated document that may be scraped or cached must produce
byte-identical output for identical input; wall-clock fallbacks defeat
conditional GET / ETag caching.

**What didn't work:** empty-feed `atom:updated` fell back to `DateTime.UtcNow`,
so an empty feed's bytes differed every scrape. Fixed to `DateTime.UnixEpoch`
(commit c1deda8), pinned by a two-render determinism test asserting the exact
literal `1970-01-01T00:00:00Z` — bytes, not just non-nullness.

**Apply when:** any surface emitting a document a client may cache/scrape
(feeds, metadata endpoints, catalog emulation).
**Prevention:** a fixed sentinel or operator-supplied value, never a wall clock;
test compares two consecutive renders for byte identity. Relevant to RSS slice 3
(conditional GET / ETag).

## Note for RSS slice 3 (already propagated via task comment)

FeedException messages embed schema identifiers (table/column names) — slice 3's
HTTP endpoint must treat FeedException as internal per
`protocol-adapter-security.md` invariant 3 (sanitize; log detail server-side);
only FeedRequestException is deliberately user-facing. Route both through one
error funnel (invariant 10): FeedRequestException -> 400, FeedException ->
sanitized 404/500.
</content>
