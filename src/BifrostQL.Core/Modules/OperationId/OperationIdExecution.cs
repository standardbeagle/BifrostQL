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
        /// cannot record one (a sync, delta or filtered update): refused rather than run without
        /// the record the caller asked for.
        /// </summary>
        public const string UnsupportedCode = "OPERATION_ID_UNSUPPORTED";

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
        /// refuse) and the recorded result is returned: no write, transformer or hook runs.</para>
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

            return await OperationWriteScope.RunAsync(connFactory, async scope =>
            {
                var recorded = await OperationIdStore.FindAsync(
                    store, dialect, scope.Connection, scope.Transaction, owner, operationId, cancellationToken);
                if (recorded is not null && recorded.Fingerprint == fingerprint)
                {
                    authorizeReplay();
                    return RecordedResult(recorded.ResultJson);
                }
                await OperationIdStore.ClaimAsync(
                    store, dialect, scope.Connection, scope.Transaction, owner, operationId, fingerprint, cancellationToken);
                var result = await write(scope);
                await OperationIdStore.RecordResultAsync(
                    store, dialect, scope.Connection, scope.Transaction, owner, operationId,
                    CanonicalJson.Serialize(result), cancellationToken);
                return result;
            }, cancellationToken);
        }

        /// <summary>
        /// Who owns an id as the store keys it: the tenant claim the read and write paths scope
        /// by (<see cref="TenantFilterTransformer.ResolveTenantContextKey"/>) and the caller's
        /// user id (<see cref="MetadataKeys.Auth.DefaultUserIdContextKey"/>), each empty when the
        /// caller has none. Either claim that is not a single value is refused: recording under
        /// a guessed owner would let one caller's id answer another's.
        /// </summary>
        internal static OperationOwner OwnerOf(IDbModel model, IDictionary<string, object?> userContext)
            => new(
                ScalarClaim(userContext, TenantFilterTransformer.ResolveTenantContextKey(model), "tenant"),
                ScalarClaim(userContext, MetadataKeys.Auth.DefaultUserIdContextKey, "user id"));

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

    /// <summary>The owner an operation id is recorded under: tenant and caller, each empty when absent.</summary>
    internal readonly record struct OperationOwner(string Tenant, string Caller);
}
