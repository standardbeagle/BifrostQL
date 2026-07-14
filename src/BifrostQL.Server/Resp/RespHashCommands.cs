using BifrostQL.Core.Resolvers;

namespace BifrostQL.Server.Resp
{
    /// <summary>
    /// The RESP hash commands (HGETALL/HGET) — a Bifrost row projected as a Redis field/value hash.
    /// Both REUSE the slice-2 single-row read path verbatim: the same key parser, the same composite-PK
    /// mapping (<see cref="TableFilter.FromPrimaryKey"/>, never <c>[0]</c>), and the same
    /// <see cref="RespReadEngine.ResolveRowsAsync"/> call through <c>IQueryIntentExecutor</c> under the
    /// session identity — so tenant isolation, soft-delete and column policy run unconditionally and no
    /// new database path exists. They differ from GET only in the WIRE SHAPE of a found row (a hash
    /// instead of a JSON bulk string) and in the authoritative column set they expose.
    ///
    /// <para><b>Column visibility is the load-bearing invariant.</b> The row the executor returns has the
    /// transformer pipeline already applied; a policy-denied or crypto-masked column is absent or masked
    /// in that row. These commands therefore build the hash strictly from the RETURNED columns
    /// (<see cref="RespReadEngine.VisibleFields"/> / <see cref="RespReadEngine.TryResolveVisibleField"/>),
    /// never re-adding columns from <see cref="Core.Model.IDbTable.Columns"/> that the pipeline dropped and
    /// never unmasking a masked value. A missing row and a tenant-hidden row are indistinguishable (both →
    /// empty hash / Null), and an existing-but-denied column is indistinguishable from an unknown field
    /// (both → Null), so neither a hidden row nor a denied column ever leaks its existence.</para>
    /// </summary>
    internal abstract class RespHashCommandHandler : RespReadCommandHandler
    {
        /// <summary>The RESP2 null bulk (<c>$-1</c>) or the RESP3 Null (<c>_</c>), per the negotiated protocol.</summary>
        protected static RespValue NullValue(int protocol) =>
            protocol == RespProtocol.Resp3 ? RespNull.Instance : RespValue.NullBulk;

        /// <summary>Encodes one column value: its scalar text as a bulk string, or the protocol Null for a SQL NULL.</summary>
        protected static RespValue FieldValue(object? value, int protocol) =>
            value is null ? NullValue(protocol) : RespValue.Bulk(RespReadEngine.FieldValueText(value));
    }

    /// <summary>
    /// <c>HGETALL &lt;table&gt;:&lt;pk…&gt;</c> — the row's visible columns as a field/value hash: a RESP3
    /// Map (<c>%</c>) of column→value when the client negotiated <c>HELLO 3</c>, else a RESP2 flat array
    /// (<c>*</c>) of alternating field,value bulk strings (Redis' HGETALL wire shape). A missing OR
    /// tenant-hidden key returns an empty hash (empty map / empty array) — indistinguishable, no leak.
    /// </summary>
    internal sealed class RespHGetAllCommandHandler : RespHashCommandHandler
    {
        public override string Name => RespProtocol.HGetAll;

        protected override RespValue? ValidateArity(int argumentCount) => RequireArgs(argumentCount, 2, Name);

        protected override async Task<RespValue> ExecuteAsync(
            RespCommandContext context, IQueryIntentExecutor executor, IReadOnlyList<RespKey> keys, CancellationToken cancellationToken)
        {
            var rows = await RespReadEngine.ResolveRowsAsync(
                executor, keys, context.Session.UserContext, context.Endpoint, cancellationToken);
            var protocol = context.Session.Protocol;

            // Missing key OR a row the identity cannot see: an empty hash, per Redis HGETALL semantics.
            if (rows[0] is not { } row)
                return EmptyHash(protocol);

            var fields = RespReadEngine.VisibleFields(row, keys[0].Table);
            return protocol == RespProtocol.Resp3 ? Map(fields, protocol) : FlatArray(fields, protocol);
        }

        private static RespValue EmptyHash(int protocol) =>
            protocol == RespProtocol.Resp3
                ? new RespMap(Array.Empty<KeyValuePair<RespValue, RespValue>>())
                : new RespArray(Array.Empty<RespValue>());

        private static RespValue Map(IReadOnlyList<KeyValuePair<string, object?>> fields, int protocol) =>
            new RespMap(fields
                .Select(f => new KeyValuePair<RespValue, RespValue>(RespValue.Bulk(f.Key), FieldValue(f.Value, protocol)))
                .ToList());

        private static RespValue FlatArray(IReadOnlyList<KeyValuePair<string, object?>> fields, int protocol)
        {
            var items = new List<RespValue>(fields.Count * 2);
            foreach (var (name, value) in fields)
            {
                items.Add(RespValue.Bulk(name));
                items.Add(FieldValue(value, protocol));
            }
            return new RespArray(items);
        }
    }

    /// <summary>
    /// <c>HGET &lt;table&gt;:&lt;pk…&gt; &lt;field&gt;</c> — a single visible column's value as a bulk
    /// string. Returns Null (<c>$-1</c>/<c>_</c>) when the key resolves to no visible row (missing OR
    /// tenant-hidden) OR when <c>&lt;field&gt;</c> is not a column the pipeline returned for this identity
    /// (unknown field, or an existing-but-denied column — the two are indistinguishable, so a denied
    /// column never leaks via a distinct error). A visible column whose value is SQL NULL also returns Null.
    /// </summary>
    internal sealed class RespHGetCommandHandler : RespHashCommandHandler
    {
        public override string Name => RespProtocol.HGet;

        protected override int TrailingNonKeyArgumentCount => 1; // the trailing <field> is not a key

        protected override RespValue? ValidateArity(int argumentCount) => RequireArgs(argumentCount, 3, Name);

        protected override async Task<RespValue> ExecuteAsync(
            RespCommandContext context, IQueryIntentExecutor executor, IReadOnlyList<RespKey> keys, CancellationToken cancellationToken)
        {
            var rows = await RespReadEngine.ResolveRowsAsync(
                executor, keys, context.Session.UserContext, context.Endpoint, cancellationToken);
            var protocol = context.Session.Protocol;
            var field = context.Arguments[2];

            return rows[0] is { } row
                   && RespReadEngine.TryResolveVisibleField(row, keys[0].Table, field, out var value)
                ? FieldValue(value, protocol)
                : NullValue(protocol);
        }
    }
}
