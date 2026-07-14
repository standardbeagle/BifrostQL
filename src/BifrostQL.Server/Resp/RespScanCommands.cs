using System.Globalization;
using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BifrostQL.Server.Resp
{
    /// <summary>
    /// <c>SCAN &lt;cursor&gt; MATCH &lt;table&gt;:* [COUNT n] [TYPE t]</c> — cursor-paginated primary-key
    /// enumeration of one table, returning the Redis SCAN reply: a 2-element array
    /// <c>[&lt;next-cursor&gt;, &lt;array of &lt;table&gt;:&lt;pk…&gt; keys&gt;]</c>. Enumeration runs through
    /// <see cref="IQueryIntentExecutor"/> under the session identity (this command requires an established
    /// identity — the connection loop answers NOAUTH before the handler runs), so the tenant/policy/
    /// soft-delete transformer pipeline is unskippable: only PKs of rows the identity may see are ever
    /// emitted, on every page.
    ///
    /// <para><b>Behavior of the option surface.</b> MATCH is REQUIRED and only <c>&lt;table&gt;:*</c> is
    /// supported (a partial glob such as <c>users:1*</c>, or a missing MATCH, is a clean <c>-ERR</c> — never
    /// a silent over-broad enumeration). An unknown table is a clean <c>-ERR</c>, consistent with the slice-2
    /// read commands (never executed against an unvalidated name). COUNT is a page-size HINT, clamped to
    /// <see cref="RespProtocol.MaxScanPageSize"/> (default <see cref="RespProtocol.DefaultScanPageSize"/>); a
    /// non-positive COUNT is a syntax error. TYPE is accepted and ignored — a Bifrost row has no single Redis
    /// type, so filtering by it would be dishonest. The cursor is opaque and encodes only a PK position; a
    /// malformed cursor is a clean <c>-ERR</c> and executes nothing. Any unexpected server fault is logged and
    /// answered with a SANITIZED error, never Bifrost-internal exception text.</para>
    /// </summary>
    internal sealed class RespScanCommandHandler : IRespCommandHandler
    {
        public string Name => RespProtocol.Scan;

        public bool RequiresAuthentication => true;

        public async Task<RespValue> HandleAsync(RespCommandContext context, CancellationToken cancellationToken)
        {
            // SCAN <cursor> … : at minimum the command name and the cursor.
            if (context.Arguments.Count < 2)
                return RespValue.Err(RespProtocol.WrongArgCount(Name));

            var parsed = ParseOptions(context.Arguments, out var optionError);
            if (optionError is not null)
                return RespValue.Err(optionError);

            try
            {
                var executor = context.Services.GetRequiredService<IQueryIntentExecutor>();
                var model = await executor.GetModelAsync(context.Endpoint);

                var table = ResolveTable(model, parsed.MatchPattern, out var matchError, out var keyPrefix);
                if (table is null)
                    return RespValue.Err(matchError!);

                if (!RespScanCursor.TryDecode(parsed.Cursor, out var cursorSegments))
                    return RespValue.Err($"{RespProtocol.ErrPrefix}invalid cursor");

                IReadOnlyList<object?>? afterKey = null;
                if (cursorSegments is not null)
                {
                    afterKey = CoerceCursor(table, cursorSegments, out var cursorError);
                    if (afterKey is null)
                        return RespValue.Err(cursorError!);
                }

                var pageSize = Math.Min(parsed.Count, RespProtocol.MaxScanPageSize);
                var page = await RespScanEngine.ScanAsync(
                    executor, table, keyPrefix!, afterKey, pageSize,
                    context.Session.UserContext, context.Endpoint, cancellationToken);

                var nextCursor = page.NextAfterKey is null
                    ? RespProtocol.ScanStartCursor
                    : RespScanCursor.Encode(page.NextAfterKey);

                var keyItems = page.Keys.Select(k => (RespValue)RespValue.Bulk(k)).ToArray();
                return RespValue.Arr(RespValue.Bulk(nextCursor), RespValue.Arr(keyItems));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                (context.Services.GetService(typeof(ILoggerFactory)) as ILoggerFactory)
                    ?.CreateLogger("BifrostQL.Server.Resp." + GetType().Name)
                    .LogWarning(ex, "resp {Command} command failed", Name);
                return RespValue.Err(RespProtocol.InternalError);
            }
        }

        private readonly record struct ScanOptions(string Cursor, string? MatchPattern, int Count);

        /// <summary>
        /// Parses <c>SCAN &lt;cursor&gt; [MATCH p] [COUNT n] [TYPE t]</c>. Options may appear in any order;
        /// each takes exactly one value. A dangling option, an unknown option, a non-positive/unparseable
        /// COUNT, or a missing MATCH is a clean syntax error (returned via <paramref name="error"/>).
        /// </summary>
        private static ScanOptions ParseOptions(IReadOnlyList<string> arguments, out string? error)
        {
            error = null;
            var cursor = arguments[1];
            string? match = null;
            var count = RespProtocol.DefaultScanPageSize;

            for (var i = 2; i < arguments.Count; i++)
            {
                var option = arguments[i].ToUpperInvariant();
                if (i + 1 >= arguments.Count)
                {
                    error = SyntaxError;
                    return default;
                }
                var value = arguments[++i];

                switch (option)
                {
                    case RespProtocol.ScanMatchOption:
                        match = value;
                        break;
                    case RespProtocol.ScanCountOption:
                        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out count) || count < 1)
                        {
                            error = SyntaxError;
                            return default;
                        }
                        break;
                    case RespProtocol.ScanTypeOption:
                        // Accepted and ignored: a Bifrost row has no single Redis type to filter on.
                        break;
                    default:
                        error = SyntaxError;
                        return default;
                }
            }

            if (match is null)
            {
                error = $"{RespProtocol.ErrPrefix}SCAN requires MATCH <table>:*";
                return default;
            }

            return new ScanOptions(cursor, match, count);
        }

        /// <summary>
        /// Validates the MATCH pattern is exactly <c>&lt;table&gt;:*</c> for a table known to the model, and
        /// returns that table plus the exact prefix token the caller used (so emitted keys round-trip into the
        /// same namespace). A pattern that is not <c>&lt;table&gt;:*</c>, or an unknown table, is a clean error.
        /// </summary>
        private static IDbTable? ResolveTable(IDbModel model, string? pattern, out string? error, out string? keyPrefix)
        {
            error = null;
            keyPrefix = null;

            if (pattern is null || !pattern.EndsWith(RespProtocol.ScanWildcardSuffix, StringComparison.Ordinal))
            {
                error = $"{RespProtocol.ErrPrefix}unsupported MATCH pattern '{pattern}'; only <table>:* is supported";
                return null;
            }

            var token = pattern[..^RespProtocol.ScanWildcardSuffix.Length];
            if (token.Length == 0 || token.IndexOfAny(GlobAndSeparatorChars) >= 0)
            {
                error = $"{RespProtocol.ErrPrefix}unsupported MATCH pattern '{pattern}'; only <table>:* is supported";
                return null;
            }

            var table = model.Tables.FirstOrDefault(t =>
                string.Equals(t.DbName, token, StringComparison.OrdinalIgnoreCase)
                || string.Equals(t.GraphQlName, token, StringComparison.OrdinalIgnoreCase));
            if (table is null)
            {
                error = $"{RespProtocol.ErrPrefix}unknown table '{token}'";
                return null;
            }

            keyPrefix = token;
            return table;
        }

        /// <summary>
        /// Coerces the cursor's PK-position segments to the table's key-column CLR types (reusing the slice-2
        /// key coercion), so the resume predicate binds as typed parameters. An arity mismatch or an
        /// unparseable segment yields null with <paramref name="error"/> set — the cursor is never executed.
        /// </summary>
        private static IReadOnlyList<object?>? CoerceCursor(IDbTable table, IReadOnlyList<string> segments, out string? error)
        {
            error = null;
            var keyColumns = table.KeyColumns.ToList();
            if (segments.Count != keyColumns.Count)
            {
                error = $"{RespProtocol.ErrPrefix}invalid cursor";
                return null;
            }

            var values = new object?[keyColumns.Count];
            for (var i = 0; i < keyColumns.Count; i++)
            {
                if (!RespReadEngine.TryCoerceKeySegment(keyColumns[i], segments[i], out var value, out _))
                {
                    error = $"{RespProtocol.ErrPrefix}invalid cursor";
                    return null;
                }
                values[i] = value;
            }
            return values;
        }

        private static readonly char[] GlobAndSeparatorChars =
            { '*', '?', '[', ']', RespProtocol.KeySeparator };

        private static string SyntaxError => $"{RespProtocol.ErrPrefix}syntax error";
    }
}
