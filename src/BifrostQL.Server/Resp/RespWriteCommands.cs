using System.Globalization;
using System.Text.Json;
using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BifrostQL.Server.Resp
{
    /// <summary>
    /// Base for the RESP key-space WRITE commands (SET/HSET/DEL) — the front door's opt-in row
    /// mutation surface. Owns the machinery every write command shares, and it is deliberately
    /// fail-closed at three layers before any database work happens:
    ///
    /// <list type="number">
    /// <item><b>Off-by-default gate.</b> Unless <see cref="RespWireOptions.EnableWrites"/> is
    /// explicitly true, EVERY write returns a clean <c>-ERR</c> and executes NOTHING — no intent is
    /// ever built. This is checked first, before arity, before parsing.</item>
    /// <item><b>Identity.</b> All write commands require an established identity (the connection loop
    /// answers NOAUTH before a handler runs), and every mutation executes under
    /// <see cref="RespSession.UserContext"/>, so the mutation transformer chain — tenant scoping,
    /// the audit ACTOR (updated_by/deleted_by), soft-delete, field-encryption-on-write, CDC/history
    /// hooks — applies unconditionally and cannot be skipped. A row outside the identity's tenant/
    /// policy scope is narrowed out by the pipeline's WHERE predicate, so a write targeting it
    /// affects ZERO rows: the adapter never writes outside scope.</item>
    /// <item><b>Containment.</b> A parse/validation failure returns a clean, honest <c>-ERR</c>; an
    /// unexpected server-side fault (incl. a pipeline write-deny/computed-column rejection) is logged
    /// and answered with a SANITIZED error, never Bifrost-internal exception text — a hostile or
    /// malformed command can neither crash the connection loop nor leak schema/driver detail.</item>
    /// </list>
    ///
    /// <para>Writes route ONLY through <see cref="IMutationIntentExecutor"/> (which delegates the full
    /// <c>TableMutationPipeline</c> chain); the model needed to validate the table/columns/PK is
    /// resolved through <see cref="IQueryIntentExecutor.GetModelAsync"/> — the adapter's model seam,
    /// backed by the same endpoint cache — so table/column names are validated against the schema and
    /// never concatenated into SQL, and values bind as mutation parameters.</para>
    /// </summary>
    internal abstract class RespWriteCommandHandler : IRespCommandHandler
    {
        public abstract string Name { get; }

        public bool RequiresAuthentication => true;

        public async Task<RespValue> HandleAsync(RespCommandContext context, CancellationToken cancellationToken)
        {
            // GATE 1 — off by default. No arity check, no parse, no intent: a disabled write surface
            // does nothing at all and cannot be probed for schema shape.
            var options = context.Services.GetRequiredService<RespWireOptions>();
            if (!options.EnableWrites)
                return RespValue.Err(RespProtocol.WritesDisabledError);

            var arityError = ValidateArity(context.Arguments.Count);
            if (arityError is not null)
                return arityError;

            try
            {
                // Model resolution reuses the read seam's cache-backed model getter; execution goes
                // through the mutation seam. Both key off the same endpoint, so the model the keys are
                // validated against is the model the mutation executes against.
                var model = await context.Services.GetRequiredService<IQueryIntentExecutor>()
                    .GetModelAsync(context.Endpoint);
                var executor = context.Services.GetRequiredService<IMutationIntentExecutor>();

                return await ExecuteAsync(context, model, executor, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFailure(context, ex);
                return RespValue.Err(RespProtocol.InternalError);
            }
        }

        /// <summary>Returns a clean <c>-ERR</c> when the argument count is wrong, otherwise null.</summary>
        protected abstract RespValue? ValidateArity(int argumentCount);

        /// <summary>Produces the reply, building and executing the mutation intent(s) under the session identity.</summary>
        protected abstract Task<RespValue> ExecuteAsync(
            RespCommandContext context, IDbModel model, IMutationIntentExecutor executor, CancellationToken cancellationToken);

        private void LogFailure(RespCommandContext context, Exception exception) =>
            (context.Services.GetService(typeof(ILoggerFactory)) as ILoggerFactory)
                ?.CreateLogger("BifrostQL.Server.Resp." + GetType().Name)
                .LogWarning(exception, "resp {Command} command failed", Name);
    }

    /// <summary>
    /// <c>SET &lt;table&gt;:&lt;pk…&gt; &lt;json&gt;</c> — sets the addressed row's columns from a JSON
    /// object mirroring slice-2's GET JSON shape (column name → value). Routed as an <b>UPDATE</b>
    /// intent through the mutation pipeline under the session identity; it does <b>not</b> insert a
    /// missing row (an addressed-but-absent or out-of-scope PK is narrowed out by the pipeline and the
    /// write is a no-op) — the safe, non-surprising choice for a first write path, and it never
    /// fabricates rows the pipeline's required columns/defaults were not asked to supply. The primary
    /// key comes from the KEY, not the JSON body: a PK column present in the JSON must equal the key
    /// value (a conflict is a clean <c>-ERR</c>) and is not treated as a SET column. The JSON must set
    /// at least one non-PK column. Reply is <c>+OK</c> when the row was actually updated, and RESP nil
    /// when the write affected ZERO rows — the addressed row is missing OR narrowed out of the caller's
    /// tenant/policy scope by the pipeline, the two indistinguishable exactly as GET reports a hidden
    /// row (so a scoped-away write never leaks another tenant's row existence, and never lies with +OK).
    /// </summary>
    internal sealed class RespSetCommandHandler : RespWriteCommandHandler
    {
        public override string Name => RespProtocol.SetCommand;

        protected override RespValue? ValidateArity(int argumentCount) =>
            argumentCount == 3 ? null : RespValue.Err(RespProtocol.WrongArgCount(Name));

        protected override async Task<RespValue> ExecuteAsync(
            RespCommandContext context, IDbModel model, IMutationIntentExecutor executor, CancellationToken cancellationToken)
        {
            var parse = RespReadEngine.ParseKey(model, context.Arguments[1]);
            if (!parse.Ok)
                return RespValue.Err(parse.Error!);
            var key = parse.Key!;

            if (!RespWriteEngine.TryParseJsonColumns(context.Arguments[2], out var json, out var jsonError))
                return RespValue.Err(jsonError!);

            var data = RespWriteEngine.BuildUpdateColumns(key, json, out var columnError);
            if (columnError is not null)
                return RespValue.Err(columnError);
            if (data.Count == 0)
                return RespValue.Err($"{RespProtocol.ErrPrefix}SET requires at least one non-primary-key column value");

            var result = await executor.ExecuteAsync(
                RespWriteEngine.UpdateIntent(key, data, context.Session.UserContext, context.Endpoint), cancellationToken);

            // Reply from the REAL affected-row count, never the intent's Value (which is the primary KEY
            // on a single-key table). A zero-affected write — missing row OR scoped away — replies nil.
            return RespWriteEngine.WroteRow(result) ? RespValue.Simple(RespProtocol.Ok) : RespValue.NullBulk;
        }
    }

    /// <summary>
    /// <c>HSET &lt;table&gt;:&lt;pk…&gt; &lt;field&gt; &lt;value&gt; [&lt;field&gt; &lt;value&gt; …]</c> —
    /// updates the named columns of the addressed row via an <b>UPDATE</b> intent through the mutation
    /// pipeline under the session identity. Each field is validated against the table's columns (unknown
    /// column → clean <c>-ERR</c>, never executed); a primary-key column cannot be set through HSET (the
    /// PK comes from the key) and is refused. Values arrive as wire strings and bind as mutation
    /// parameters (the pipeline coerces to the column's type). Reply is the integer count of fields
    /// written (the number of field/value pairs supplied) when the row was actually updated, and <c>0</c>
    /// when the write affected ZERO rows — the addressed row is missing OR narrowed out of the caller's
    /// tenant/policy scope, the two indistinguishable so a scoped-away write neither leaks a hidden row's
    /// existence nor reports a phantom field count.
    /// </summary>
    internal sealed class RespHSetCommandHandler : RespWriteCommandHandler
    {
        public override string Name => RespProtocol.HSet;

        protected override RespValue? ValidateArity(int argumentCount) =>
            // HSET key field value [field value …]: the command name + key + at least one even pair.
            argumentCount >= 4 && (argumentCount - 2) % 2 == 0 ? null : RespValue.Err(RespProtocol.WrongArgCount(Name));

        protected override async Task<RespValue> ExecuteAsync(
            RespCommandContext context, IDbModel model, IMutationIntentExecutor executor, CancellationToken cancellationToken)
        {
            var parse = RespReadEngine.ParseKey(model, context.Arguments[1]);
            if (!parse.Ok)
                return RespValue.Err(parse.Error!);
            var key = parse.Key!;

            var fields = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 2; i < context.Arguments.Count; i += 2)
            {
                var column = RespWriteEngine.ResolveWritableColumn(key.Table, context.Arguments[i], out var columnError);
                if (column is null)
                    return RespValue.Err(columnError!);
                fields[column.GraphQlName] = context.Arguments[i + 1];
            }

            var result = await executor.ExecuteAsync(
                RespWriteEngine.UpdateIntent(key, fields, context.Session.UserContext, context.Endpoint), cancellationToken);

            // Report the fields written only when the update actually affected a row; a zero-affected
            // write (missing OR scoped away) reports 0, never the supplied field count.
            return RespValue.Int(RespWriteEngine.WroteRow(result) ? fields.Count : 0);
        }
    }

    /// <summary>
    /// <c>DEL &lt;key&gt; [&lt;key&gt; …]</c> — deletes the addressed rows via a <b>DELETE</b> intent per
    /// key through the mutation pipeline under the session identity. The adapter routes a delete; the
    /// pipeline decides hard vs soft (a table with soft-delete metadata is soft-deleted by its
    /// transformer — the adapter never special-cases it). Every key is parsed and validated up front;
    /// a single malformed key (unknown table, wrong PK arity, unparseable segment) is a clean
    /// <c>-ERR</c> and deletes NOTHING. Reply is the integer count of keys that actually deleted a row:
    /// a key whose row is missing or outside the identity's scope is narrowed out by the pipeline
    /// (affected 0) and is not counted.
    /// </summary>
    internal sealed class RespDelCommandHandler : RespWriteCommandHandler
    {
        public override string Name => RespProtocol.Del;

        protected override RespValue? ValidateArity(int argumentCount) =>
            argumentCount >= 2 ? null : RespValue.Err(RespProtocol.WrongArgCount(Name));

        protected override async Task<RespValue> ExecuteAsync(
            RespCommandContext context, IDbModel model, IMutationIntentExecutor executor, CancellationToken cancellationToken)
        {
            // Parse-then-execute: validate every key before deleting any, so one bad key rejects the
            // whole command without a partial delete.
            var keys = new List<RespKey>(context.Arguments.Count - 1);
            for (var i = 1; i < context.Arguments.Count; i++)
            {
                var parse = RespReadEngine.ParseKey(model, context.Arguments[i]);
                if (!parse.Ok)
                    return RespValue.Err(parse.Error!);
                keys.Add(parse.Key!);
            }

            var deleted = 0;
            foreach (var key in keys)
            {
                var result = await executor.ExecuteAsync(
                    RespWriteEngine.DeleteIntent(key, context.Session.UserContext, context.Endpoint), cancellationToken);
                deleted += RespWriteEngine.DeletedRowCount(result);
            }
            return RespValue.Int(deleted);
        }
    }

    /// <summary>
    /// Shared write-intent construction behind the RESP write commands. Turns a parsed
    /// <see cref="RespKey"/> plus request columns into a <see cref="MutationIntent"/> whose positional
    /// <see cref="MutationIntent.PrimaryKey"/> carries the key's PK values IN SCHEMA ORDER (composite-PK
    /// safe — never <c>[0]</c>) and whose <see cref="MutationIntent.UserContext"/> is the session
    /// identity, so tenant scoping and the audit actor resolve from the caller. Column names are
    /// validated against the table's model columns; values are carried as data and bound as parameters
    /// by the pipeline — never concatenated into SQL.
    /// </summary>
    internal static class RespWriteEngine
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        /// <summary>Builds the UPDATE intent: SET columns in <paramref name="data"/>, PK from the key, identity carried.</summary>
        public static MutationIntent UpdateIntent(
            RespKey key, IReadOnlyDictionary<string, object?> data, IDictionary<string, object?> userContext, string? endpoint) =>
            new()
            {
                Table = key.Table.DbName,
                Action = MutationIntentAction.Update,
                Data = data,
                PrimaryKey = key.KeyValues,
                UserContext = new Dictionary<string, object?>(userContext),
                Endpoint = endpoint,
            };

        /// <summary>Builds the DELETE intent: predicate is the positional PK from the key, identity carried.</summary>
        public static MutationIntent DeleteIntent(
            RespKey key, IDictionary<string, object?> userContext, string? endpoint) =>
            new()
            {
                Table = key.Table.DbName,
                Action = MutationIntentAction.Delete,
                Data = new Dictionary<string, object?>(),
                PrimaryKey = key.KeyValues,
                UserContext = new Dictionary<string, object?>(userContext),
                Endpoint = endpoint,
            };

        /// <summary>
        /// Parses a SET body as a flat JSON object of column → scalar value. A non-object, malformed
        /// JSON, or a nested object/array value is a clean, client-safe error (never executed). Scalars
        /// map to CLR primitives (string / long / decimal / bool / null) the mutation binder coerces.
        /// </summary>
        public static bool TryParseJsonColumns(string body, out IReadOnlyDictionary<string, JsonElement> json, out string? error)
        {
            json = new Dictionary<string, JsonElement>();
            error = null;
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(body);
            }
            catch (JsonException)
            {
                error = $"{RespProtocol.ErrPrefix}value is not valid JSON";
                return false;
            }

            using (doc)
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    error = $"{RespProtocol.ErrPrefix}SET value must be a JSON object of column values";
                    return false;
                }

                var map = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in doc.RootElement.EnumerateObject())
                    map[property.Name] = property.Value.Clone();
                json = map;
                return true;
            }
        }

        /// <summary>
        /// Turns the validated JSON columns into the UPDATE data set. Each JSON key must resolve to a
        /// model column (DB or GraphQL name; unknown → clean error). A primary-key column is accepted
        /// only when its value equals the key's value for that column (a conflict → clean error) and is
        /// dropped from the data — the PK comes from the key, never the body. Non-PK columns become the
        /// SET data, keyed by GraphQL name and carrying CLR-scalar values.
        /// </summary>
        public static Dictionary<string, object?> BuildUpdateColumns(
            RespKey key, IReadOnlyDictionary<string, JsonElement> json, out string? error)
        {
            error = null;
            var data = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            var keyColumns = key.Table.KeyColumns.ToList();

            foreach (var (name, element) in json)
            {
                var column = FindColumn(key.Table, name);
                if (column is null)
                {
                    error = $"{RespProtocol.ErrPrefix}unknown column '{name}' on table '{key.Table.DbName}'";
                    return data;
                }
                if (!TryConvertScalar(element, out var value, out var valueError))
                {
                    error = $"{RespProtocol.ErrPrefix}column '{column.ColumnName}': {valueError}";
                    return data;
                }

                var keyIndex = keyColumns.FindIndex(c =>
                    string.Equals(c.ColumnName, column.ColumnName, StringComparison.OrdinalIgnoreCase));
                if (keyIndex >= 0)
                {
                    // A PK column in the body is only allowed if it agrees with the key; it never becomes
                    // a SET column (the WHERE key is authoritative), so a SET can't move a row's identity.
                    if (!ScalarEquals(value, key.KeyValues[keyIndex]))
                    {
                        error = $"{RespProtocol.ErrPrefix}primary-key column '{column.ColumnName}' in the body " +
                                $"does not match the key";
                        return data;
                    }
                    continue;
                }
                data[column.GraphQlName] = value;
            }
            return data;
        }

        /// <summary>
        /// Resolves an HSET field name to a writable model column: a known column (DB or GraphQL name)
        /// that is NOT part of the primary key (a PK is set via the key, never HSET). Unknown or PK
        /// columns yield null with a clean, client-safe error.
        /// </summary>
        public static ColumnDto? ResolveWritableColumn(IDbTable table, string fieldName, out string? error)
        {
            error = null;
            var column = FindColumn(table, fieldName);
            if (column is null)
            {
                error = $"{RespProtocol.ErrPrefix}unknown column '{fieldName}' on table '{table.DbName}'";
                return null;
            }
            if (table.KeyColumns.Any(c => string.Equals(c.ColumnName, column.ColumnName, StringComparison.OrdinalIgnoreCase)))
            {
                error = $"{RespProtocol.ErrPrefix}cannot set primary-key column '{column.ColumnName}' via HSET; " +
                        $"it is addressed by the key";
                return null;
            }
            return column;
        }

        /// <summary>
        /// Whether an UPDATE intent actually changed a row, read from the ONLY trustworthy signal —
        /// <see cref="MutationIntentResult.AffectedRows"/>. The intent's <see cref="MutationIntentResult.Value"/>
        /// is the primary KEY on a single-key table (not a count), so reading it as a count is inert for
        /// every nonzero key and misfires on key value 0. A null <see cref="MutationIntentResult.AffectedRows"/>
        /// is treated as "did not write" (fail-closed): a write is only reported successful on a positive count.
        /// </summary>
        public static bool WroteRow(MutationIntentResult result) => result.AffectedRows is > 0;

        /// <summary>
        /// The affected-row count a DELETE intent reports (0 when tenant/policy scope made it a no-op).
        /// For a delete the pipeline puts the affected count in <see cref="MutationIntentResult.Value"/>
        /// itself (matching the GraphQL delete field) and leaves <c>AffectedRows</c> null — the reverse of
        /// update, so this reads <c>Value</c> deliberately, not by the mistake update must avoid.
        /// </summary>
        public static int DeletedRowCount(MutationIntentResult result) =>
            result.Value is null ? 0 : Convert.ToInt32(result.Value, CultureInfo.InvariantCulture);

        private static ColumnDto? FindColumn(IDbTable table, string name) =>
            table.Columns.FirstOrDefault(c =>
                string.Equals(c.DbName, name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(c.GraphQlName, name, StringComparison.OrdinalIgnoreCase));

        private static bool TryConvertScalar(JsonElement element, out object? value, out string error)
        {
            error = string.Empty;
            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    value = element.GetString();
                    return true;
                case JsonValueKind.Number:
                    value = element.TryGetInt64(out var l) ? l : element.GetDecimal();
                    return true;
                case JsonValueKind.True:
                case JsonValueKind.False:
                    value = element.GetBoolean();
                    return true;
                case JsonValueKind.Null:
                    value = null;
                    return true;
                default:
                    value = null;
                    error = "value must be a JSON scalar (string, number, boolean, or null)";
                    return false;
            }
        }

        /// <summary>Culture-invariant scalar equality for reconciling a body PK value against the key's coerced value.</summary>
        private static bool ScalarEquals(object? a, object? b)
        {
            if (a is null || b is null)
                return a is null && b is null;
            var ta = Convert.ToString(a, CultureInfo.InvariantCulture);
            var tb = Convert.ToString(b, CultureInfo.InvariantCulture);
            if (string.Equals(ta, tb, StringComparison.Ordinal))
                return true;
            // Numeric values may differ only in scale/type (e.g. body 1 vs coerced 1L); compare as decimals when possible.
            return decimal.TryParse(ta, NumberStyles.Number, CultureInfo.InvariantCulture, out var da)
                && decimal.TryParse(tb, NumberStyles.Number, CultureInfo.InvariantCulture, out var db)
                && da == db;
        }
    }
}
