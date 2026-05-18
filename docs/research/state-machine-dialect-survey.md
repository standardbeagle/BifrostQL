# State-machine + workflow runtime: dialect-coupling survey

**Purpose:** Catalogue every code path in the state-machine + workflow subsystem that emits SQL directly. Counterpart to `agg-dialect-survey.md`. Tracks which surfaces (if any) bypass `ISqlDialect` and would break on non-SqlServer engines.

## Trigger surface

| Component | Role |
|---|---|
| `Auth/StateMachineConfigCollector.cs` | Parses state-machine definitions from `IMetadataSource`. Pure data, no SQL. |
| `Auth/StateMachineDefinition.cs` | DTO: states, transitions, role-qualified guards. No SQL. |
| `Auth/StateTransitionInfo.cs` | DTO captured at transition time. No SQL. |
| `Modules/StateMachineMutationTransformer.cs` | `IMutationTransformer` that gates updates against the state machine. Operates on `MutationTransformContext.Data`, never composes SQL directly. |
| `Modules/IMutationObserver.cs` | Observer chain (`MutationObservers`, `StateTransitionObservers`). Fires post-commit; observer side-effects use `IBifrostWorkflowExecutor.InsertAsync(...)`. |
| `Server/StateTransitionAuditObserver.cs` | Writes one audit row per transition via `IBifrostWorkflowExecutor.InsertAsync("audit_log", ...)`. **No direct SQL.** |
| `Server/IBifrostWorkflowExecutor.cs` | Abstraction over the mutation pipeline. Implementation routes through `DbTableInsertResolver`, which is fully dialect-aware. |
| `Workflows/IWorkflowRunner.cs`, `WorkflowDefinition.cs`, `WorkflowScheduler.cs`, `WorkflowTriggerHost.cs` | Workflow runtime: definitions, scheduler, trigger host. None emit SQL; all data motion goes through `IWorkflowDataExecutor`, which in turn routes through the GraphQL mutation pipeline. |

## Hardcoded dialect tokens

**None found.** Every data write in the state-machine + workflow path flows through `IBifrostWorkflowExecutor.InsertAsync` or `IWorkflowDataExecutor`, which delegate to `DbTableInsertResolver`. The resolver consumes `ISqlDialect.EscapeIdentifier`, `TableReference`, and `ReturningIdentityClause`/`LastInsertedIdentity` — every cross-dialect leak the `_agg` audit found was fixed in the dialect-aware refactor.

The only direct string formatting is in `StateTransitionAuditObserver.cs` line 30:

```csharp
["created_at"] = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
```

This is a *value*, not SQL. It is bound as a parameter through the mutation pipeline. Sqlite, Postgres, SqlServer, and MySQL all accept this ISO-ish format on TEXT/TIMESTAMP/DATETIME2/DATETIME columns. Worth a follow-up to switch to `DateTime.UtcNow.ToString("o", ...)` for full ISO-8601 with timezone, but not a portability blocker.

## Cross-dialect risks (not in SQL emission)

1. **`audit_log` table assumption.** `StateTransitionAuditObserver` writes to a table literally named `audit_log` with these columns: `action`, `entity_type`, `entity_id`, `actor_user_id`, `summary`, `created_at`, `tenant_id`. The seeds for SqlServer/Sqlite/Postgres/MySQL must all expose a table with that name and at least those columns or the audit row insert errors out.
2. **`tenant_id` column type.** The observer calls `ToAuditNumericValue(tenantId)` which returns either a numeric type or the original (possibly string) value. If the audit table types `tenant_id` as INT but a string slipped through, the insert errors per dialect. SqlServer rejects implicit string→int conversion; Postgres rejects without an explicit cast; MySQL coerces silently. Worth pinning at the resolver, not the observer.

## Test coverage today

| Test | Status |
|---|---|
| `StateMachineMutationTransformerTests` | passes (unit) |
| `StateMachineConfigCollectorTests` | passes (unit) |
| `StateTransitionObserverTests` (incl. fail-soft) | passes (unit) |
| `WorkflowDefinitionTests`, `WorkflowRunnerTests`, `WorkflowTriggerTests` | pass (unit, using `CapturingWorkflowRunner`) |
| `MembershipManagerStateMachineBypassTests` | passes against in-memory Sqlite via the HostedSpa sample |
| **Any** e2e GraphQL mutation that triggers a state transition + asserts the audit row landed in the real DB | **none for SqlServer/Postgres/MySQL** |

The HostedSpa-mediated test exercises the full chain on Sqlite, but the other three engines never run the audit-write path under test. Worktrack task **"End-to-end integration tests for state-machine + workflow subsystem"** captures the gap.

## Refactor plan implied by this audit

1. Add an e2e test against Sqlite that mutates an entity through a state transition and asserts an `audit_log` row matching `StateTransitionAuditObserver`'s expected shape. Use the HostedSpa sample's wiring if applicable.
2. Mirror to SqlServer, Postgres, MySQL once the Sqlite test is green.
3. Switch the `created_at` format string to `"o"` for full ISO-8601, and add a guard in `ToAuditNumericValue` so non-numeric tenant ids surface a clear error instead of relying on the dialect to coerce.
4. Document the `audit_log` table shape as part of the deployment checklist for any BifrostQL host that enables `AddBifrostStateMachineAudit`.

## Open gaps

- Untested e2e path on three of the four engines.
- No documented schema for the `audit_log` table.
- `ToAuditNumericValue` silently falls back to the original value when parsing fails; on MySQL this yields silent type coercion that masks bugs.
