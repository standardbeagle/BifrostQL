# Paged Nested Collections + edit-db Model B

> Worktree: main `/home/beagle/work/core/bifrost`. Clean-rebuild before trusting tests (WSL2). Backend dev = pnpx vite via .agnt.kdl; don't run competing :5000.

**Goal:** Give nested multi-link child collections the same paged contract as top-level queries (`limit/offset/sort` args + `<child>_paged { total offset limit data }` return), with **per-parent** paging. Then switch edit-db's child drill-down to **model B**: traverse parent→child so the server injects the polymorphic discriminator (client joins by id only) — removing the client-side discriminator hack and fixing multi-col FK + parent filtering generally.

**Why breaking is acceptable (user decision):** uniform paged contract for all collections; the cross-type leak and multi-col-FK drill-downs are fixed structurally.

## Baseline facts (from exploration)
- Schema: `TableSchemaGenerator.cs:89-94` emits multi-link `name(filter:T): [Child]`. Top-level paged type `GetPagedTableTypeDefinition()` (`:126-136`) = `type <T>_paged { data:[<T>] total:Int! offset:Int limit:Int }`. Top-level field args `limit,offset,sort,filter` (`:40-46`).
- Parse: `QueryField.ToSqlData` (`:60-111`) reads `limit/offset/sort/filter`; sets `IncludeResult=true` ONLY top-level (`parent==null`).
- Top-level total: separate `…=>count` SQL (`GqlObjectQuery.AddSqlParameterized:46-101`); assembled in `SqlExecutionManager:113-126` into `TableResult{Total,Offset,Limit,Data}`.
- Nested exec: `GqlObjectQuery.ToConnectedSqlParameterized:116-145` — one flat INNER JOIN, global LIMIT (not per-parent). `ReaderEnum.GetJoinResult:52-66` returns `SubTableEnumerable` (bare array, in-memory filtered by parent key `JoinKeyMatcher.FilterRows`). No ROW_NUMBER anywhere.
- m2m fields also emit `: [Target]` (`:99`) — SEPARATE concern; keep as array this round unless a shared code path forces otherwise. Document if touched.

## Slice PN-A — server: paged nested multi-link collections
One cohesive change (schema+parse+SQL+result move together).

1. **Schema** (`TableSchemaGenerator.cs:93`): multi-link field →
   `{fieldName}(filter: {Child.TableFilterTypeName}, limit: Int, offset: Int, sort: [{Child.TableColumnSortEnumName}!]) : {Child.GraphQlName}_paged`
   Reuse existing `_paged` type. Leave m2m (`:99`) as `[Target]` for now.
2. **Parse** (`QueryField.ToSqlData`): for a multi-link nested field (QueryType.Join via a MultiLink), set `IncludeResult=true` so a per-parent total is produced, and ensure the `data { … }` sub-selection maps to the child's scalar/link fields (mirror how top-level `data` pseudo-field is parsed — find that handling and reuse). `total/offset/limit` pseudo-fields resolve from the wrapper.
3. **SQL** (`ToConnectedSqlParameterized`): when the connected (child) query `IncludeResult`, emit per-parent window paging:
   `ROW_NUMBER() OVER (PARTITION BY <srcId cols> ORDER BY <sort|stable key>)` then filter `rn BETWEEN offset+1 AND offset+limit`; and a per-parent COUNT (`COUNT(*) … GROUP BY <srcId>`) keyed for assembly. All 4 dialects support window fns — put shared SQL in the dialect base; only override if syntax differs (SQLite/PG/MySQL/SqlServer all support `ROW_NUMBER() OVER`). Keep non-paged path (no limit/offset/sort requested) working — but since the return type is now `_paged`, the wrapper is always produced; default limit when absent (mirror top-level default).
4. **Result** (`ReaderEnum.GetJoinResult` / `SqlExecutionManager`): multi-link join returns a paged wrapper object `{total,offset,limit,data}` (data = the SubTableEnumerable filtered per parent), not a bare enumerable. Reuse `TableResult` (make accessible if needed).
5. **Fix all consumers/tests**: schema-text tests (e.g. `PolymorphicLinkTests` asserting `notes(filter: …) : [notes]` → `notes_paged`), any Core/Server/UI test selecting nested arrays now needs `{ data { … } }`. `PolymorphicLeakRepro` query becomes `companies{ data { notes { data { entity_type } } } }`; update extraction. Run ALL suites; fix every break (intention-preserving). Clean rebuild first.

Gate: Core + Server + UI all green.

## Slice PN-B — edit-db model B drill-down
1. Drill-down for a parent→child relationship: query the PARENT (`parent(filter:{pk _eq id}){ data { <childField>(limit,offset,sort) { total offset limit data { … } } } }`) and unwrap `parent.data[0].<childField>`. Works for polymorphic (server injects discriminator via ConnectLinks) and multi-col FK (parent PK match, no child FK column needed).
2. Remove the Slice-7 client discriminator hack (`lib/polymorphic.ts` buildChildDrillDownFilter discriminator branch) — server now scopes. Keep `_dbSchema` `isPolymorphic` only if still used for badging; else drop.
3. Result shape: nested child is now `_paged` ({total,offset,limit,data}) → the related grid regains server pagination/sort. Wire useDataTable to read the nested paged wrapper.
4. Tests: edit-db vitest for the new parent-traversal query builder; update existing. `pnpx vite build` rebuilds dist.

Gate: edit-db vitest green, dist rebuilt; live: company notes show only company rows AND paginate.

## Risks
- Per-parent window SQL is the hard part; verify per-parent limit (parent A's limit doesn't bleed into B) with a seeded multi-parent test.
- Default limit on nested: pick same default as top-level to avoid unbounded fetches.
- m2m left as array → inconsistency; note for a follow-up.
- edit-db nested selections elsewhere (single links unaffected; only multi-link arrays change).
