using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BifrostQL.Server.Resp
{
    /// <summary>
    /// Base for the RESP key-space READ commands (GET/MGET/EXISTS/TYPE). Owns the machinery every
    /// read command shares: the auth gate (all read commands require an established identity — the
    /// connection loop answers NOAUTH before a handler ever runs, and the intent executes under
    /// <see cref="RespSession.UserContext"/> so the security pipeline is unskippable), argument-count
    /// validation, model resolution, key parsing, and — critically — exception containment. A parse
    /// failure returns a clean, honest <c>-ERR</c>; an unexpected server-side fault is logged and
    /// answered with a SANITIZED error (never Bifrost-internal exception text, which can carry
    /// schema/driver detail), so a malformed or hostile command can neither crash the connection loop
    /// nor leak internals.
    /// </summary>
    internal abstract class RespReadCommandHandler : IRespCommandHandler
    {
        public abstract string Name { get; }

        public bool RequiresAuthentication => true;

        public async Task<RespValue> HandleAsync(RespCommandContext context, CancellationToken cancellationToken)
        {
            var arityError = ValidateArity(context.Arguments.Count);
            if (arityError is not null)
                return arityError;

            try
            {
                var executor = context.Services.GetRequiredService<IQueryIntentExecutor>();
                var model = await executor.GetModelAsync(context.Endpoint);

                var keys = ParseKeys(model, context.Arguments, out var parseError);
                if (parseError is not null)
                    return RespValue.Err(parseError);

                return await ExecuteAsync(context, executor, keys, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFailure(context, ex);
                return RespValue.Err(RespProtocol.InternalError);
            }
        }

        /// <summary>Returns a clean <c>-ERR</c> when the argument count is wrong, otherwise null.</summary>
        protected abstract RespValue? ValidateArity(int argumentCount);

        /// <summary>Produces the reply from the already-parsed, validated keys.</summary>
        protected abstract Task<RespValue> ExecuteAsync(
            RespCommandContext context, IQueryIntentExecutor executor, IReadOnlyList<RespKey> keys, CancellationToken cancellationToken);

        /// <summary>The single-key commands (GET/TYPE) resolve exactly one key.</summary>
        protected static RespValue? RequireExactArgs(int argumentCount, string command) =>
            argumentCount == 2 ? null : RespValue.Err(RespProtocol.WrongArgCount(command));

        /// <summary>The multi-key commands (MGET/EXISTS) resolve one or more keys.</summary>
        protected static RespValue? RequireAtLeastOneKey(int argumentCount, string command) =>
            argumentCount >= 2 ? null : RespValue.Err(RespProtocol.WrongArgCount(command));

        private static IReadOnlyList<RespKey> ParseKeys(IDbModel model, IReadOnlyList<string> arguments, out string? error)
        {
            var keys = new List<RespKey>(arguments.Count - 1);
            for (var i = 1; i < arguments.Count; i++)
            {
                var parse = RespReadEngine.ParseKey(model, arguments[i]);
                if (!parse.Ok)
                {
                    // Fail fast on the first malformed key: honest, and never executes any key.
                    error = parse.Error;
                    return keys;
                }
                keys.Add(parse.Key!);
            }
            error = null;
            return keys;
        }

        private void LogFailure(RespCommandContext context, Exception exception) =>
            (context.Services.GetService(typeof(ILoggerFactory)) as ILoggerFactory)
                ?.CreateLogger("BifrostQL.Server.Resp." + GetType().Name)
                .LogWarning(exception, "resp {Command} command failed", Name);
    }

    /// <summary>
    /// <c>GET &lt;table&gt;:&lt;pk…&gt;</c> — a single-row primary-key lookup. Returns the row as a
    /// JSON bulk string, or a RESP Null when the key resolves to no visible row (missing OR filtered
    /// out by tenant/policy — the two are indistinguishable, so a hidden row's existence never leaks).
    /// </summary>
    internal sealed class RespGetCommandHandler : RespReadCommandHandler
    {
        public override string Name => RespProtocol.Get;

        protected override RespValue? ValidateArity(int argumentCount) => RequireExactArgs(argumentCount, Name);

        protected override async Task<RespValue> ExecuteAsync(
            RespCommandContext context, IQueryIntentExecutor executor, IReadOnlyList<RespKey> keys, CancellationToken cancellationToken)
        {
            var rows = await RespReadEngine.ResolveRowsAsync(
                executor, keys, context.Session.UserContext, context.Endpoint, cancellationToken);
            var row = rows[0];
            return row is null
                ? RespValue.NullBulk
                : RespValue.Bulk(RespReadEngine.RowToJson(row, keys[0].Table));
        }
    }

    /// <summary>
    /// <c>MGET &lt;key&gt; [&lt;key&gt; …]</c> — a batched multi-key lookup. Returns a RESP array of
    /// JSON bulk strings / Nulls positionally aligned to the requested keys; keys of the same
    /// single-PK table collapse into one <c>_in</c> intent. Each key is still tenant-filtered.
    /// </summary>
    internal sealed class RespMGetCommandHandler : RespReadCommandHandler
    {
        public override string Name => RespProtocol.MGet;

        protected override RespValue? ValidateArity(int argumentCount) => RequireAtLeastOneKey(argumentCount, Name);

        protected override async Task<RespValue> ExecuteAsync(
            RespCommandContext context, IQueryIntentExecutor executor, IReadOnlyList<RespKey> keys, CancellationToken cancellationToken)
        {
            var rows = await RespReadEngine.ResolveRowsAsync(
                executor, keys, context.Session.UserContext, context.Endpoint, cancellationToken);
            var items = new RespValue[rows.Count];
            for (var i = 0; i < rows.Count; i++)
                items[i] = rows[i] is { } row
                    ? RespValue.Bulk(RespReadEngine.RowToJson(row, keys[i].Table))
                    : RespValue.NullBulk;
            return RespValue.Arr(items);
        }
    }

    /// <summary>
    /// <c>EXISTS &lt;key&gt; [&lt;key&gt; …]</c> — the count of keys resolving to a visible row. A row
    /// the identity cannot see counts as not-existing, exactly like a missing key.
    /// </summary>
    internal sealed class RespExistsCommandHandler : RespReadCommandHandler
    {
        public override string Name => RespProtocol.Exists;

        protected override RespValue? ValidateArity(int argumentCount) => RequireAtLeastOneKey(argumentCount, Name);

        protected override async Task<RespValue> ExecuteAsync(
            RespCommandContext context, IQueryIntentExecutor executor, IReadOnlyList<RespKey> keys, CancellationToken cancellationToken)
        {
            var rows = await RespReadEngine.ResolveRowsAsync(
                executor, keys, context.Session.UserContext, context.Endpoint, cancellationToken);
            return RespValue.Int(rows.Count(r => r is not null));
        }
    }

    /// <summary>
    /// <c>TYPE &lt;key&gt;</c> — the Redis type of the key. A Bifrost row is modeled as a JSON string
    /// value, so a visible row is <c>string</c> and a missing/invisible key is <c>none</c>.
    /// </summary>
    internal sealed class RespTypeCommandHandler : RespReadCommandHandler
    {
        public override string Name => RespProtocol.Type;

        protected override RespValue? ValidateArity(int argumentCount) => RequireExactArgs(argumentCount, Name);

        protected override async Task<RespValue> ExecuteAsync(
            RespCommandContext context, IQueryIntentExecutor executor, IReadOnlyList<RespKey> keys, CancellationToken cancellationToken)
        {
            var rows = await RespReadEngine.ResolveRowsAsync(
                executor, keys, context.Session.UserContext, context.Endpoint, cancellationToken);
            return RespValue.Simple(rows[0] is null ? RespProtocol.TypeNone : RespProtocol.TypeString);
        }
    }
}
