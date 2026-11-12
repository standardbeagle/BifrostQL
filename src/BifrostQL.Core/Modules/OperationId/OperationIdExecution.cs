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
        /// Claims <paramref name="operationId"/>, runs <paramref name="write"/> inside one
        /// operation scope, and records its result. A write that throws rolls the claim back
        /// with it, so a failed write leaves no record.
        /// </summary>
        public static async Task<object?> ExecuteAsync(
            IDbModel model, IDbConnFactory connFactory, IDictionary<string, object?> userContext,
            string operationId, OperationFingerprint fingerprint,
            Func<OperationWriteScope, Task<object?>> write, CancellationToken cancellationToken)
        {
            // ModelConfigValidator refuses a model whose opted-in tables have no store, so a miss
            // here is a model that skipped validation: fail closed rather than write unrecorded.
            var store = OperationIdConfig.FindStore(model)
                ?? throw new BifrostExecutionError("The operation id could not be recorded, so the write did not run.");
            var tenant = TenantOf(model, userContext);
            var dialect = connFactory.Dialect;

            return await OperationWriteScope.RunAsync(connFactory, async scope =>
            {
                await OperationIdStore.ClaimAsync(
                    store, dialect, scope.Connection, scope.Transaction, tenant, operationId, fingerprint, cancellationToken);
                var result = await write(scope);
                await OperationIdStore.RecordResultAsync(
                    store, dialect, scope.Connection, scope.Transaction, tenant, operationId,
                    CanonicalJson.Serialize(result), cancellationToken);
                return result;
            }, cancellationToken);
        }

        /// <summary>
        /// The caller's tenant as the store keys it: the tenant claim the read and write paths
        /// scope by (<see cref="TenantFilterTransformer.ResolveTenantContextKey"/>), or empty for
        /// a caller with none. A claim that is not a single value is refused: recording under a
        /// guessed tenant would let one tenant's id answer another's.
        /// </summary>
        internal static string TenantOf(IDbModel model, IDictionary<string, object?> userContext)
        {
            var key = TenantFilterTransformer.ResolveTenantContextKey(model);
            if (!userContext.TryGetValue(key, out var value) || value is null)
                return string.Empty;
            return value switch
            {
                string s => s,
                Guid g => g.ToString("D"),
                IConvertible c when value is not System.Collections.IEnumerable => c.ToString(CultureInfo.InvariantCulture),
                _ => throw new BifrostExecutionError("The caller's tenant is not a single value, so the operation id cannot be recorded.")
                {
                    ErrorCode = BifrostExecutionError.AccessDeniedCode,
                },
            };
        }

        private static BifrostExecutionError Invalid(string message)
            => new(message) { ErrorCode = BifrostExecutionError.InvalidValueCode };
    }
}
