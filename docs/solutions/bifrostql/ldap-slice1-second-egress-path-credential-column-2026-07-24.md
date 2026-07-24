---
written_at: 2026-07-24T02:05:00Z
source_event: task:01KXM41EFE0SH3CVWCHBJGKMG6
module: bifrostql
category: security
confidence: high
form: constraint
sources:
  - task:01KXM41EFE0SH3CVWCHBJGKMG6#comment-01KY8WKAFR84QCDD5HFVG3E040   # review_annotation_v1 attempt-1 FAIL: systemicObservations (second-egress DN path)
  - task:01KXM41EFE0SH3CVWCHBJGKMG6#comment-01KY8XG25MF9KSFPXT4W40Z5R8   # review_annotation_v1 attempt-2 PASS: passPatterns + cross-table structural closure
  - git:edf17d6   # RED pin: DN-naming-column credential leak proven against guard-removed code
  - git:7168bb9   # GREEN fix + recorded full-egress sweep
related:
  - .claude/rules/protocol-adapter-security.md          # invariants 9 (catch-set symmetry) + 10 (single funnel, condition-tagged) — same "cover the SET of sibling paths" family
  - .claude/rules/regression-test-non-vacuous.md        # the fixture that decouples the two egress channels is the non-vacuous requirement
  - docs/solutions/bifrostql/s3-slice1-address-vs-storage-key-2026-07-16.md   # "consult in-repo prior art before building a second seam over the same resource"
  - docs/solutions/bifrostql/crypto-blind-index-read-routing-2026-07-24.md    # backstop-shadow / revert-proof-must-target-the-specific-branch
tags: [ldap, protocol-adapter, security, credential-column, second-egress-path, egress-sweep, revert-proof, non-vacuous, dialect-blind-type, module-slice, rewind]
status: steering
recurrence: 1
---

# LDAP slice 1: a never-expose guard must cover EVERY egress path, not the obvious one (1 rewind)

**Task**: LDAP slice 1 — directory mapping contract + DN/schema validation
(01KXM41EFE0SH3CVWCHBJGKMG6). Security review FAIL attempt 1 (1 major
blocker) → rework → PASS attempt 2. Contract+validation only; no wire yet.

## Lessons

1. **A "never expose column X" invariant must be enforced against the SET of
   every config-referenceable egress path for X's value — not just the
   obvious returned-field list.** The credential (password-hash) column guard
   covered the `ldap-attributes` list but not the `ldap-dn-template` RDN
   naming column. `uid={password_hash}` + `ldap-attributes: uid=username` +
   `ldap-credential: password_hash` validated CLEAN: the `uid` *attribute*
   returned a benign column so the attributes-list guard saw nothing, while
   every entry's *DN* literally carried the password hash — and a DN is
   returned on every search result and is enumerable. The naming column is a
   second, independent egress the first guard structurally could not see.
   This is the same family as `protocol-adapter-security` invariant 9
   (catch-set symmetry across sibling op classes) and invariant 10 (one
   funnel is necessary-not-sufficient; every path must tag the same
   condition) — "guard/catch the SET of sibling paths, not the first-found
   one" — now recurring on a *config-egress* surface rather than an
   error-mapping one.

2. **Fixing one named path is not the fix — sweep every config-driven egress
   and RECORD the per-path conclusion.** The reviewer named one path (DN
   naming column); the rework enumerated ALL config-referenceable column
   egresses and stated why each is safe (this recorded sweep is the reusable
   deliverable, cheap to re-verify at review):
   - DN-template RDN naming column — WAS unguarded → now guarded (the blocker).
   - `ldap-attributes` list — already guarded (pre-existing check).
   - `ldap-base-dn` / static DN segments — literal strings; the RDN grammar
     forbids `{placeholder}` outside the one leftmost component, so no column
     egress and no multi-placeholder bypass.
   - `ldap-member` — names a relationship, not a column; member DNs derive
     from the TARGET table's naming column, never the group's credential
     column.
   - RootDSE/subschema (`LdapDirectoryModel`) — enumerate only the
     already-guarded `ldap-attributes` plus a synthesized literal `member`.
   - objectClasses — literal name strings.
   NamingColumn was the only unguarded path. Guard it at the SAME parse
   boundary that already owns the invariant (`ParseCredentialColumn`),
   comparing post-canonicalization `OrdinalIgnoreCase` (closes case /
   DB-casing drift) so a future egress is added in one place.

3. **Structural closure is a composition property worth stating.** Cross-table
   member DN egress is closed *structurally*, not by remembering to check:
   `RequireMappedMemberTarget` demands the member target itself be
   LDAP-mapped, so the target table's OWN `ParseCredentialColumn` guard
   already rejects naming==credential on that table. Future cross-table value
   flow (memberOf, referrals) must preserve the "target must be mapped"
   requirement or it re-opens the hole.

4. **The non-vacuous fixture must DECOUPLE the two egress channels.** A guard
   proven non-vacuous on one path says nothing about a sibling path — so the
   pinning fixture maps the `uid` attribute to a BENIGN column while the RDN
   names by the credential column. If the fixture let the attribute also name
   the credential column, the pre-existing attributes-list guard would mask
   the DN-path leak and the test would be vacuous. RED-commit-first (edf17d6
   pins the leak against guard-removed code; 7168bb9 adds the guard) made the
   commit itself the guard-removed proof; the reviewer independently repeated
   the revert and got the exact leak signature. (Per
   `.claude/rules/regression-test-non-vacuous.md` — the "vacuous fixture on
   one path" trap generalizes from single-test coverage to guard-coverage.)

5. **(Advisory / fact) Wire-type/syntax classification from a bare `DataType`
   string is dialect-blind.** `timestamp` means datetime on Postgres/MySQL
   but rowversion (opaque binary) on SQL Server; classifying it as
   `GeneralizedTime` passes syntax validation while carrying binary. Fixed to
   `OctetString` per SQL Server semantics (the ambiguity is unresolvable from
   the bare type string); also removed a dead `timestamp_binary` entry
   `StringNormalizer.NormalizeType` never produces. Any adapter deriving
   wire-type classes from raw `DataType` strings without dialect context
   inherits this ambiguity — a dialect-aware map is the real fix if a dialect
   seam ever reaches `ColumnSyntax`.

## Apply when

- Any protocol-adapter slice with a sensitive-column exclusion (credential,
  encrypted, hidden, PII columns) — enumerate EVERY surface that can emit a
  column's value: returned attributes/fields, entry names / DNs / RDNs,
  member / relationship references, sort keys, group-by labels, exported
  identifiers, subschema/introspection, error/log text. Write the guard as an
  egress-path checklist, not a single-list check.
- Any config surface where one secret/column has multiple independent
  exposure routes: reject them all at the ONE parse boundary that owns the
  invariant, canonicalize-then-compare.
- Deriving wire types from raw `DataType` strings (lesson 5).

## Prevention (checklist to add to future slices)

- [ ] For each never-expose column, list every config key that can reference a
      column and confirm the guard covers each (record the per-path
      conclusion in the completion, like this slice's `egressSweep`).
- [ ] Guard at the parse boundary that already owns the invariant; compare
      canonicalized names `OrdinalIgnoreCase`.
- [ ] Prefer grammar-level containment (one placeholder site, static segments
      reject `{}`) so new egress paths cannot be added by configuration alone
      — it makes the sweep short.
- [ ] Pinning fixture must decouple the channels so no sibling guard masks the
      path under test; RED-commit-first + narrate the revert-proof.
- [ ] When the LDAP wire slices land, add a conformance fact asserting the
      credential column value never appears in any emitted DN or attribute
      value end-to-end (model-load guard is necessary; a wire-level assertion
      pins the whole chain).

## Promotion candidate

The "guard/catch must cover the SET of sibling egress paths, not the
first-found one" family now has ≥3 independent manifestations in-repo
(`protocol-adapter-security` invariant 9 catch-set symmetry, invariant 10
single-funnel-condition-tagging, and this config-egress instance). When the
recurrence gate clears, fold a config-egress invariant (12) into
`.claude/rules/protocol-adapter-security.md` so the checklist reaches every
adapter, not just those that read this steering doc. Flagged for operator
decision; registered as steering only for now.
