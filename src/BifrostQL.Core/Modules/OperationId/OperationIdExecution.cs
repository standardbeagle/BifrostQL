using System.Data.Common;
using System.Globalization;
using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;

namespace BifrostQL.Core.Modules.OperationId
{
    /// <summary>
    /// Runs a write that carries a caller operation id: claims the id in the store, runs the
    /// write through the mutation pipeline on the same transaction
    /// (<see cref="OperationWriteScope"/>), and records the field's result, so the write and
    /// its record commit together or roll back together.
    /// </summary>
    internal static class OperationIdExecution
    {
        /// <summary>
        /// <see cref="BifrostExecutionError.ErrorCode"/> for an operation id on a mutation that
        /// cannot record one (a sync, delta or filtered update, or a save whose tree reaches a
        /// table that requires approval): refused rather than run without the record the caller
        /// asked for.
        /// </summary>
        public const string UnsupportedCode = "OPERATION_ID_UNSUPPORTED";

        /// <summary>
        /// <see cref="BifrostExecutionError.ErrorCode"/> for an id the caller already recorded
        /// for a different write (another table, verb, target key or arguments): refused, never
        /// replayed and never run, so a client can tell a reused id from a key collision.
        /// </summary>
        public const string ReusedCode = "OPERATION_ID_REUSED";

        /// <summary>
        /// <see cref="BifrostExecutionError.ErrorCode"/> for a write without an operation id to a
        /// table that declares <see cref="MetadataKeys.OperationId.Required"/>: refused before
        /// any transformer runs, so nothing is written.
        /// </summary>
        public const string RequiredCode = "OPERATION_ID_REQUIRED";

        /// <summary>
        /// <see cref="BifrostExecutionError.ErrorCode"/> for an operation id from a caller without
        /// a user id: refused before the claim, so nothing is written. Such callers cannot be
        /// told apart, so a shared id space would answer one with another's recorded result.
        /// </summary>
        public const string AnonymousCode = "OPERATION_ID_ANONYMOUS";

        /// <summary>
        /// The operation id a field carries for <paramref name="table"/>: the
        /// <see cref="OperationIdConfig.Argument"/> argument or the request's
        /// <see cref="OperationIdConfig.Header"/> header. Null when the table does not opt in or
        /// the request carries no id. Both present and different, blank, or longer than
        /// <see cref="OperationIdConfig.MaxLength"/> is refused as an invalid value.
        /// </summary>
        public static string? Resolve(IBifrostFieldContext context, IDbTable table)
        {
            if (OperationIdConfig.ModeOf(table) == OperationIdMode.None)
                return null;

            var fromArgument = context.HasArgument(OperationIdConfig.Argument)
                ? context.GetArgument<string?>(OperationIdConfig.Argument)
                : null;
            var fromHeader = context.UserContext.TryGetValue(OperationIdConfig.UserContextKey, out var header)
                ? header as string
                : null;

            if (fromArgument is not null && fromHeader is not null && !string.Equals(fromArgument, fromHeader, StringComparison.Ordinal))
                throw Invalid("The operation id argument and the Idempotency-Key header carry different values.");

            var id = fromArgument ?? fromHeader;
            if (id is null)
                return null;
            if (string.IsNullOrWhiteSpace(id))
                throw Invalid("An operation id must not be blank.");
            if (id.Length > OperationIdConfig.MaxLength)
                throw Invalid($"An operation id must be at most {OperationIdConfig.MaxLength} characters.");
            return id;
        }

        /// <summary>
        /// Answers a retry of a recorded write, or claims <paramref name="operationId"/>, runs
        /// <paramref name="write"/> inside one operation scope, and records its result. A write
        /// that throws rolls the claim back with it, so a failed write leaves no record.
        ///
        /// <para>When the caller already holds a committed record for the id with the same
        /// <paramref name="fingerprint"/>, <paramref name="authorizeReplay"/> runs (it throws to
        /// refuse) and the recorded result is returned: no write, transformer or hook runs. A
        /// record with a different fingerprint is refused with <see cref="ReusedCode"/>.</para>
        ///
        /// <para>Two requests claiming one id at once: the second's claim waits on the first's
        /// uncommitted record. If the first rolls back, the waiting claim succeeds in place and the
        /// second writes. If the first commits, the claim fails; that attempt rolls back and runs
        /// once more in a fresh transaction, which sees the committed record and answers as a
        /// retry would (replay or <see cref="ReusedCode"/>). A claim that conflicts again (only
        /// when the store carries an extra unique key, or a record vanished) is refused as a
        /// plain <see cref="BifrostExecutionError.ConflictCode"/> that names no store constraint.</para>
        ///
        /// <para>Three requests claiming one id while the first rolls back: the two waiting claims
        /// can deadlock on MySQL. The victim runs once more in a fresh transaction, where its claim
        /// waits on the survivor's and then proceeds as above. A second deadlock is not retried.</para>
        /// </summary>
        public static async Task<object?> ExecuteAsync(
            IDbModel model, IDbConnFactory connFactory, IDictionary<string, object?> userContext,
            string operationId, OperationFingerprint fingerprint, Action authorizeReplay,
            Func<OperationWriteScope, Task<object?>> write, CancellationToken cancellationToken)
        {
            // ModelConfigValidator refuses a model whose opted-in tables have no store, so a miss
            // here is a model that skipped validation: fail closed rather than write unrecorded.
            var store = OperationIdConfig.FindStore(model)
                ?? throw new BifrostExecutionError("The operation id could not be recorded, so the write did not run.");
            var owner = OwnerOf(model, userContext);
            var dialect = connFactory.Dialect;

            var claimLost = false;
            var claimDeadlocked = false;
            var deadlockRetried = false;
            var lastAttempt = false;
            while (true)
            {
                claimLost = claimDeadlocked = false;
                try
                {
                    return await RunOnceAsync(lastAttempt);
                }
                catch (BifrostExecutionError) when (claimLost)
                {
                    // A concurrent request claimed the same id first and committed while this claim
                    // waited on it. This transaction is spent (Postgres has aborted it), so a fresh
                    // one reads the committed record: replayed, or refused as reused.
                    lastAttempt = true;
                }
                catch (BifrostExecutionError) when (claimDeadlocked)
                {
                    // Two claims waited on a third that rolled back: InnoDB grants both a shared
                    // lock on the released key, both then need the exclusive one, and this one was
                    // chosen as the deadlock victim (MySQL 1213). The survivor is still running, so
                    // a fresh attempt waits on its claim like any second claim would.
                    deadlockRetried = true;
                }
            }

            Task<object?> RunOnceAsync(bool lastAttempt) => OperationWriteScope.RunAsync(connFactory, async scope =>
            {
                var recorded = await OperationIdStore.FindAsync(
                    store, dialect, scope.Connection, scope.Transaction, owner, operationId, cancellationToken);
                if (recorded is not null)
                {
                    if (recorded.Fingerprint != fingerprint)
                        throw new BifrostExecutionError("This operation id was already used for a different write.")
                        {
                            ErrorCode = ReusedCode,
                        };
                    authorizeReplay();
                    return RecordedResult(recorded.ResultJson);
                }
                try
                {
                    await OperationIdStore.ClaimAsync(
                        store, dialect, scope.Connection, scope.Transaction, owner, operationId, fingerprint, cancellationToken);
                }
                catch (Exception ex) when (BifrostExecutionError.IsUniqueViolation(ex))
                {
                    if (!lastAttempt)
                    {
                        claimLost = true;
                        throw;
                    }
                    // No record answers this claim, so the conflict is on the store itself. The
                    // driver error names a constraint of the hidden store: keep it off the wire.
                    throw new BifrostExecutionError(BifrostExecutionError.ConflictMessage, ex)
                    {
                        ErrorCode = BifrostExecutionError.ConflictCode,
                    };
                }
                catch (Exception ex) when (!deadlockRetried && IsDeadlock(ex))
                {
                    claimDeadlocked = true;
                    throw;
                }
                var result = await write(scope);
                await OperationIdStore.RecordResultAsync(
                    store, dialect, scope.Connection, scope.Transaction, owner, operationId,
                    CanonicalJson.Serialize(result), cancellationToken);
                return result;
            }, cancellationToken);
        }

        /// <summary>
        /// Whether <paramref name="ex"/> (or an inner exception) is a driver error saying the
        /// database chose this transaction as a deadlock victim. Read from the driver's code, never
        /// its text, so an error that only mentions a deadlock is not retried: SqlState
        /// <c>40001</c> (MySQL 1213) or <c>40P01</c> (PostgreSQL), and for SQL Server, which
        /// reports no SqlState, error 1205 in <see cref="Exception.Data"/>. Read through
        /// <see cref="DbException"/> because this assembly references no driver.
        /// </summary>
        internal static bool IsDeadlock(Exception ex)
        {
            for (var current = ex; current is not null; current = current.InnerException)
            {
                if (current is DbException { SqlState: SerializationFailureSqlState or DeadlockDetectedSqlState })
                    return true;
                if (current is DbException { SqlState: null } db
                    && db.Data[SqlServerErrorNumberKey] is SqlServerDeadlockVictimNumber)
                    return true;
            }
            return false;
        }

        /// <summary>SQLSTATE 40001, which MySQL reports for deadlock error 1213.</summary>
        private const string SerializationFailureSqlState = "40001";

        /// <summary>PostgreSQL SQLSTATE 40P01 <c>deadlock_detected</c>.</summary>
        private const string DeadlockDetectedSqlState = "40P01";

        /// <summary>
        /// The <see cref="Exception.Data"/> key under which Microsoft.Data.SqlClient's
        /// <c>SqlException</c> carries the server error number.
        /// </summary>
        private const string SqlServerErrorNumberKey = "HelpLink.EvtID";

        /// <summary>SQL Server error 1205: the transaction was chosen as the deadlock victim.</summary>
        private const string SqlServerDeadlockVictimNumber = "1205";

        /// <summary>
        /// Adds every module argument a field carries to a fingerprint's
        /// <paramref name="arguments"/>, under <c>module:</c>-prefixed names, except the operation
        /// id itself: two requests that differ in any module argument differ.
        /// </summary>
        internal static void AddModuleArguments(
            Dictionary<string, object?> arguments, IReadOnlyDictionary<string, object?> moduleArguments)
        {
            foreach (var (key, value) in moduleArguments)
            {
                if (key != OperationIdModuleApi.ContextKey)
                    arguments["module:" + key] = value;
            }
        }

        /// <summary>
        /// The authorization a replayed write still passes, per write it recorded: the policy's
        /// table action and column write refusals (<see cref="PolicyMutationTransformer.Refuse"/>)
        /// for <paramref name="action"/> over the columns <paramref name="input"/> writes (its key
        /// from <paramref name="primaryKeyValues"/> when given). An upsert recorded only its verb,
        /// not which branch ran, so it must pass both the create and the update check. Row-level
        /// checks (row scope, self deny, tenant filter) need the row and the chain, which a replay
        /// does not run; the record's owner key already confines it to the caller and tenant that
        /// wrote it. A batch asks this per action and a save per tree node. The input is rekeyed
        /// from GraphQL field names to column names first, as the live chain does in
        /// <see cref="MutationTransformersWrap.TransformAsync"/>: the policy names columns, so a
        /// column whose name differs from its field would otherwise escape its refusal.
        /// </summary>
        internal static void AuthorizeReplay(
            IDbTable table, MutationAction action, IReadOnlyDictionary<string, object?> input,
            IReadOnlyList<object?>? primaryKeyValues, IDictionary<string, object?> userContext)
        {
            var data = DbParameterBinder.ToDbColumnKeys(table, action == MutationAction.Insert
                ? input
                : MutationArgumentBinder.SplitProperties(table, input, primaryKeyValues).data);
            MutationType[] checks = action switch
            {
                MutationAction.Insert => [MutationType.Insert],
                MutationAction.Update => [MutationType.Update],
                MutationAction.Delete => [MutationType.Delete],
                MutationAction.Upsert => [MutationType.Insert, MutationType.Update],
                _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
            };
            foreach (var check in checks)
                PolicyMutationTransformer.Refuse(table, check, data, userContext)?.ThrowIfDenied();
        }

        /// <summary>
        /// Who owns an id as the store keys it: the tenant claim the read and write paths scope
        /// by (<see cref="TenantFilterTransformer.ResolveTenantContextKey"/>), empty when the
        /// caller has none, and the caller's user id
        /// (<see cref="MetadataKeys.Auth.DefaultUserIdContextKey"/>). A caller without a user id
        /// is refused with <see cref="AnonymousCode"/>, and either claim that is not a single
        /// value is refused: recording under a shared or guessed owner would let one caller's id
        /// answer another's.
        /// </summary>
        internal static OperationOwner OwnerOf(IDbModel model, IDictionary<string, object?> userContext)
        {
            var tenant = ScalarClaim(userContext, TenantFilterTransformer.ResolveTenantContextKey(model), "tenant");
            var caller = ScalarClaim(userContext, MetadataKeys.Auth.DefaultUserIdContextKey, "user id");
            if (string.IsNullOrWhiteSpace(caller))
                throw new BifrostExecutionError("An operation id needs a caller with a user id.")
                {
                    ErrorCode = AnonymousCode,
                };
            return new(tenant, caller);
        }

        private static string ScalarClaim(IDictionary<string, object?> userContext, string key, string claimName)
        {
            if (!userContext.TryGetValue(key, out var value) || value is null)
                return string.Empty;
            return value switch
            {
                string s => s,
                Guid g => g.ToString("D"),
                IConvertible c when value is not System.Collections.IEnumerable => c.ToString(CultureInfo.InvariantCulture),
                _ => throw new BifrostExecutionError($"The caller's {claimName} is not a single value, so the operation id cannot be recorded.")
                {
                    ErrorCode = BifrostExecutionError.AccessDeniedCode,
                },
            };
        }

        /// <summary>
        /// The CLR value of a result recorded by <see cref="CanonicalJson"/>: text, a whole
        /// number as <see cref="int"/> or <see cref="long"/>, any other number as
        /// <see cref="decimal"/> (<see cref="double"/> past its range), or null. The resolver coerces it to the field's scalar exactly
        /// as it coerced the original result, so the replay serializes as the write did.
        /// </summary>
        internal static object? RecordedResult(string resultJson)
        {
            using var document = System.Text.Json.JsonDocument.Parse(resultJson);
            var value = document.RootElement;
            return value.ValueKind switch
            {
                System.Text.Json.JsonValueKind.Null => null,
                System.Text.Json.JsonValueKind.String => value.GetString(),
                System.Text.Json.JsonValueKind.Number when value.TryGetInt32(out var i) => i,
                System.Text.Json.JsonValueKind.Number when value.TryGetInt64(out var l) => l,
                System.Text.Json.JsonValueKind.Number when value.TryGetDecimal(out var d) => d,
                System.Text.Json.JsonValueKind.Number => value.GetDouble(),
                _ => throw new BifrostExecutionError("The recorded operation result cannot be replayed."),
            };
        }

        private static BifrostExecutionError Invalid(string message)
            => new(message) { ErrorCode = BifrostExecutionError.InvalidValueCode };
    }

    /// <summary>The owner an operation id is recorded under: tenant (empty when absent) and the caller's user id.</summary>
    internal readonly record struct OperationOwner(string Tenant, string Caller);
}
