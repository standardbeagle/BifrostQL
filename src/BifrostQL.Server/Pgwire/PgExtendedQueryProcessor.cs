using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using BifrostQL.Core.Resolvers;
using Microsoft.Extensions.Logging;

namespace BifrostQL.Server.Pgwire
{
    /// <summary>
    /// Per-connection driver for the PostgreSQL EXTENDED query protocol
    /// (Parse/Bind/Describe/Execute/Sync/Close), the path prepared-statement drivers use.
    /// Owns this connection's named + unnamed prepared-statement and portal maps and the
    /// "skip until Sync" recovery state the protocol mandates after an error.
    ///
    /// <para>It reuses the slice 1-4 seams verbatim: the SAME <see cref="IPgQueryTranslator"/>
    /// (so <c>$N</c> placeholders bind as DATA through <c>TableFilter.FromObject</c>, never
    /// concatenated, and the security transformer pipeline stays unskippable), the SAME
    /// <see cref="IPgCatalogResponder"/> for prepared catalog/introspection queries, and the
    /// SAME text encoders. A query-phase error is sanitized identically to the simple path
    /// (<see cref="PgQueryError"/>) and, per the extended protocol, all further messages are
    /// ignored until the client's Sync, which then answers ReadyForQuery.</para>
    ///
    /// <para><b>Format-code scope (explicit).</b> Parameter and result values are TEXT format
    /// only — the format every generic prepared-statement driver defaults to. A BINARY
    /// parameter or result format code in Bind is rejected honestly with a clean
    /// feature_not_supported ErrorResponse (then skip-until-Sync), never silently
    /// misinterpreted. Binary format is a documented follow-up.</para>
    /// </summary>
    internal sealed class PgExtendedQueryProcessor
    {
        private readonly Stream _stream;
        private readonly IDictionary<string, object?> _userContext;
        private readonly IQueryIntentExecutor _executor;
        private readonly IPgQueryTranslator _translator;
        private readonly IPgCatalogResponder? _catalog;
        private readonly string? _endpoint;
        private readonly PgCancellationRegistration _cancellation;
        private readonly CancellationToken _connectionToken;
        private readonly ILogger _logger;

        private readonly Dictionary<string, PgPreparedStatement> _statements = new(StringComparer.Ordinal);
        private readonly Dictionary<string, PgPortal> _portals = new(StringComparer.Ordinal);

        /// <summary>
        /// True after an error inside an extended sequence: every message except Sync (and
        /// the caller-handled Terminate) is discarded until the client re-synchronizes.
        /// </summary>
        public bool IsSkipping { get; private set; }

        public PgExtendedQueryProcessor(
            Stream stream,
            IDictionary<string, object?> userContext,
            IQueryIntentExecutor executor,
            IPgQueryTranslator translator,
            IPgCatalogResponder? catalog,
            string? endpoint,
            PgCancellationRegistration cancellation,
            CancellationToken connectionToken,
            ILogger logger)
        {
            _stream = stream;
            _userContext = userContext;
            _executor = executor;
            _translator = translator;
            _catalog = catalog;
            _endpoint = endpoint;
            _cancellation = cancellation;
            _connectionToken = connectionToken;
            _logger = logger;
        }

        /// <summary>
        /// Dispatches one extended-protocol frontend message. While skipping, only Sync is
        /// honored (it re-synchronizes); all else is dropped. Connection-lifecycle faults
        /// (IO/framing/connection cancellation) propagate to the outer handler; query-phase
        /// errors are turned into ErrorResponse + skip-until-Sync here.
        /// </summary>
        public async Task HandleAsync(PgFrontendMessage message, CancellationToken ct)
        {
            if (IsSkipping && message.Type != PgWireProtocol.SyncMessage)
                return; // discard until Sync

            switch (message.Type)
            {
                case PgWireProtocol.ParseMessage: await HandleParseAsync(message.Body, ct); break;
                case PgWireProtocol.BindMessage: await HandleBindAsync(message.Body, ct); break;
                case PgWireProtocol.DescribeMessage: await HandleDescribeAsync(message.Body, ct); break;
                case PgWireProtocol.ExecuteMessage: await HandleExecuteAsync(message.Body, ct); break;
                case PgWireProtocol.CloseMessage: await HandleCloseAsync(message.Body, ct); break;
                case PgWireProtocol.SyncMessage: await HandleSyncAsync(ct); break;
                case PgWireProtocol.FlushMessage: /* every frame is already flushed */ break;
                default:
                    await FailAsync(PgWireProtocol.SqlStateProtocolViolation,
                        $"unexpected extended-protocol message '{(char)message.Type}'.", ct);
                    break;
            }
        }

        // ---- Parse (P) -------------------------------------------------------

        private async Task HandleParseAsync(byte[] body, CancellationToken ct)
        {
            var reader = new Reader(body);
            var statementName = reader.ReadCString();
            var sql = reader.ReadCString();
            var paramCount = reader.ReadInt16();
            var paramOids = new int[(paramCount < 0 ? 0 : paramCount)];
            for (var i = 0; i < paramCount; i++) paramOids[i] = reader.ReadInt32();

            _statements[statementName] = new PgPreparedStatement(sql, paramOids);
            // A prepared statement replaces the destination name; any portal built from the
            // prior version is implicitly invalidated by a fresh Bind, so nothing else to do.
            await SendEmptyAsync(PgWireProtocol.ParseComplete, ct);
        }

        // ---- Bind (B) --------------------------------------------------------

        private async Task HandleBindAsync(byte[] body, CancellationToken ct)
        {
            var reader = new Reader(body);
            var portalName = reader.ReadCString();
            var statementName = reader.ReadCString();

            if (!_statements.TryGetValue(statementName, out var statement))
            {
                await FailAsync(PgWireProtocol.SqlStateInvalidSqlStatementName,
                    $"prepared statement \"{statementName}\" does not exist.", ct);
                return;
            }

            // Parameter format codes: 0 = none (all text), 1 = one code for all, N = per value.
            var formatCodeCount = reader.ReadInt16();
            var paramFormats = new short[(formatCodeCount < 0 ? 0 : formatCodeCount)];
            for (var i = 0; i < formatCodeCount; i++) paramFormats[i] = reader.ReadInt16();

            var valueCount = reader.ReadInt16();
            var values = new object?[(valueCount < 0 ? 0 : valueCount)];
            for (var i = 0; i < valueCount; i++)
            {
                var format = ResolveFormat(paramFormats, i);
                if (format != PgWireProtocol.FormatText)
                {
                    await FailAsync(PgWireProtocol.SqlStateFeatureNotSupported,
                        "binary parameter format is not supported; use text format.", ct);
                    return;
                }
                var length = reader.ReadInt32();
                if (length == PgWireProtocol.NullValueLength) { values[i] = null; continue; }
                var raw = reader.ReadBytes(length);
                var oid = i < statement.ParameterTypeOids.Length ? statement.ParameterTypeOids[i] : 0;
                try
                {
                    values[i] = DecodeTextParameter(oid, raw);
                }
                catch (FormatException)
                {
                    await FailAsync(PgWireProtocol.SqlStateProtocolViolation,
                        $"parameter ${i + 1} text value is not valid for its declared type.", ct);
                    return;
                }
            }

            // Result format codes: reject binary; text-only results in this slice.
            var resultFormatCount = reader.ReadInt16();
            for (var i = 0; i < resultFormatCount; i++)
            {
                if (reader.ReadInt16() != PgWireProtocol.FormatText)
                {
                    await FailAsync(PgWireProtocol.SqlStateFeatureNotSupported,
                        "binary result format is not supported; request text format.", ct);
                    return;
                }
            }

            _portals[portalName] = new PgPortal(statement, values);
            await SendEmptyAsync(PgWireProtocol.BindComplete, ct);
        }

        // ---- Describe (D) ----------------------------------------------------

        private async Task HandleDescribeAsync(byte[] body, CancellationToken ct)
        {
            var reader = new Reader(body);
            var target = reader.ReadByte();
            var name = reader.ReadCString();

            if (target == PgWireProtocol.DescribeStatement)
            {
                if (!_statements.TryGetValue(name, out var statement))
                {
                    await FailAsync(PgWireProtocol.SqlStateInvalidSqlStatementName,
                        $"prepared statement \"{name}\" does not exist.", ct);
                    return;
                }
                await DescribeStatementAsync(statement, ct);
            }
            else if (target == PgWireProtocol.DescribePortal)
            {
                if (!_portals.TryGetValue(name, out var portal))
                {
                    await FailAsync(PgWireProtocol.SqlStateInvalidSqlStatementName,
                        $"portal \"{name}\" does not exist.", ct);
                    return;
                }
                await DescribePortalAsync(portal, ct);
            }
            else
            {
                await FailAsync(PgWireProtocol.SqlStateProtocolViolation,
                    $"invalid Describe target '{(char)target}'.", ct);
            }
        }

        private async Task DescribeStatementAsync(PgPreparedStatement statement, CancellationToken ct)
        {
            PgQueryPlan plan;
            try
            {
                plan = await _translator.TranslateAsync(_executor, statement.Sql, parameters: null, _userContext, _endpoint, ct);
            }
            catch (Exception ex) when (IsQueryPhaseFault(ex))
            {
                await FailFromExceptionAsync(ex, ct);
                return;
            }

            // ParameterDescription: one OID per placeholder, using the Parse-declared OID
            // where the driver supplied one, else 0 (unspecified — the driver infers).
            var count = Math.Max(plan.ParameterCount, statement.ParameterTypeOids.Length);
            var oids = new int[count];
            for (var i = 0; i < count; i++)
                oids[i] = i < statement.ParameterTypeOids.Length ? statement.ParameterTypeOids[i] : 0;
            await PgProtocolIO.WriteMessageAsync(_stream, PgWireProtocol.ParameterDescription,
                PgBackend.ParameterDescription(oids), ct);

            await WriteRowDescriptionOrNoDataAsync(plan.Columns, ct);
        }

        private async Task DescribePortalAsync(PgPortal portal, CancellationToken ct)
        {
            ResolvedPortal resolved;
            try
            {
                resolved = await ResolvePortalAsync(portal, ct);
            }
            catch (Exception ex) when (IsQueryPhaseFault(ex))
            {
                await FailFromExceptionAsync(ex, ct);
                return;
            }
            await WriteRowDescriptionOrNoDataAsync(resolved.Columns, ct);
        }

        // ---- Execute (E) -----------------------------------------------------

        private async Task HandleExecuteAsync(byte[] body, CancellationToken ct)
        {
            var reader = new Reader(body);
            var portalName = reader.ReadCString();
            var maxRows = reader.ReadInt32(); // 0 = no limit

            if (!_portals.TryGetValue(portalName, out var portal))
            {
                await FailAsync(PgWireProtocol.SqlStateInvalidSqlStatementName,
                    $"portal \"{portalName}\" does not exist.", ct);
                return;
            }

            ResolvedPortal resolved;
            IReadOnlyList<IReadOnlyDictionary<string, object?>> rows;
            var queryToken = _cancellation.BeginQuery(_connectionToken);
            try
            {
                resolved = await ResolvePortalAsync(portal, queryToken);
                rows = await ResolveRowsAsync(portal, resolved, queryToken);
            }
            catch (OperationCanceledException) when (_cancellation.WasCancelRequested && !_connectionToken.IsCancellationRequested)
            {
                // A matching CancelRequest aborted this query. The session survives: emit the
                // standard query_canceled error and enter skip-until-Sync.
                await FailAsync(PgWireProtocol.SqlStateQueryCanceled, PgWireProtocol.QueryCanceledMessage, ct);
                return;
            }
            catch (Exception ex) when (IsQueryPhaseFault(ex))
            {
                await FailFromExceptionAsync(ex, ct);
                return;
            }
            finally
            {
                _cancellation.EndQuery();
            }

            await StreamPortalRowsAsync(portal, resolved, rows, maxRows, ct);
        }

        private async Task StreamPortalRowsAsync(
            PgPortal portal, ResolvedPortal resolved,
            IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, int maxRows, CancellationToken ct)
        {
            var start = portal.Position;
            var remaining = rows.Count - start;
            var take = maxRows <= 0 ? remaining : Math.Min(maxRows, remaining);

            for (var i = 0; i < take; i++)
            {
                var row = rows[start + i];
                var textValues = resolved.Columns
                    .Select(c => PgValueEncoder.ToText(row.TryGetValue(c.Name, out var v) ? v : null))
                    .ToList();
                await PgProtocolIO.WriteMessageAsync(_stream, PgWireProtocol.DataRow, PgBackend.DataRow(textValues), ct);
            }
            portal.Position = start + take;

            if (maxRows > 0 && portal.Position < rows.Count)
            {
                // The row-count limit was reached with rows still buffered: the portal stays
                // open and the next Execute resumes from Position. No CommandComplete yet.
                await SendEmptyAsync(PgWireProtocol.PortalSuspended, ct);
                return;
            }

            await PgProtocolIO.WriteMessageAsync(_stream, PgWireProtocol.CommandComplete,
                PgBackend.CommandComplete($"SELECT {take}"), ct);
        }

        // ---- Sync (S) / Close (C) -------------------------------------------

        private async Task HandleSyncAsync(CancellationToken ct)
        {
            IsSkipping = false;
            await PgProtocolIO.WriteMessageAsync(_stream, PgWireProtocol.ReadyForQuery,
                new[] { PgWireProtocol.TransactionStatusIdle }, ct);
        }

        private async Task HandleCloseAsync(byte[] body, CancellationToken ct)
        {
            var reader = new Reader(body);
            var target = reader.ReadByte();
            var name = reader.ReadCString();

            // Closing a nonexistent statement/portal is NOT an error in pg — it succeeds.
            if (target == PgWireProtocol.DescribeStatement) _statements.Remove(name);
            else if (target == PgWireProtocol.DescribePortal) _portals.Remove(name);

            await SendEmptyAsync(PgWireProtocol.CloseComplete, ct);
        }

        // ---- portal resolution ----------------------------------------------

        /// <summary>Columns + row source of a bound portal, resolved once and cached on it.</summary>
        private sealed class ResolvedPortal
        {
            public required IReadOnlyList<PgResultColumn> Columns { get; init; }
            /// <summary>Non-null for the SQL read path: execute this for rows.</summary>
            public QueryIntent? Intent { get; init; }
            /// <summary>Non-null for a catalog/introspection portal: rows already materialized.</summary>
            public IReadOnlyList<IReadOnlyDictionary<string, object?>>? CatalogRows { get; init; }
        }

        private async Task<ResolvedPortal> ResolvePortalAsync(PgPortal portal, CancellationToken ct)
        {
            if (portal.Resolved is ResolvedPortal cached) return cached;

            // A prepared catalog/introspection query (no parameters) routes through the SAME
            // catalog responder the simple path uses. Parameterized catalog queries are not
            // matched (the responder takes no bind values); they fall to the translator, which
            // rejects an unknown catalog relation honestly.
            if (_catalog is not null && portal.Parameters.Length == 0)
            {
                var response = await _catalog.TryRespondAsync(_executor, portal.Statement.Sql, _userContext, _endpoint, ct);
                if (response is not null)
                {
                    var catalogResolved = new ResolvedPortal { Columns = response.Columns, CatalogRows = response.Rows };
                    portal.Resolved = catalogResolved;
                    return catalogResolved;
                }
            }

            var plan = await _translator.TranslateAsync(_executor, portal.Statement.Sql, portal.Parameters, _userContext, _endpoint, ct);
            var resolved = new ResolvedPortal { Columns = plan.Columns, Intent = plan.Intent };
            portal.Resolved = resolved;
            return resolved;
        }

        private async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ResolveRowsAsync(
            PgPortal portal, ResolvedPortal resolved, CancellationToken ct)
        {
            if (portal.ExecutedRows is not null) return portal.ExecutedRows;

            IReadOnlyList<IReadOnlyDictionary<string, object?>> rows;
            if (resolved.CatalogRows is not null)
            {
                rows = resolved.CatalogRows;
            }
            else
            {
                var result = await _executor.ExecuteAsync(resolved.Intent!, ct);
                rows = result.Rows;
            }
            portal.ExecutedRows = rows;
            return rows;
        }

        // ---- helpers ---------------------------------------------------------

        private async Task WriteRowDescriptionOrNoDataAsync(IReadOnlyList<PgResultColumn> columns, CancellationToken ct)
        {
            if (columns.Count == 0)
            {
                await SendEmptyAsync(PgWireProtocol.NoData, ct);
                return;
            }
            await PgProtocolIO.WriteMessageAsync(_stream, PgWireProtocol.RowDescription,
                PgBackend.RowDescription(columns), ct);
        }

        private async Task SendEmptyAsync(byte type, CancellationToken ct)
            => await PgProtocolIO.WriteMessageAsync(_stream, type, Array.Empty<byte>(), ct);

        private static short ResolveFormat(short[] formatCodes, int index) => formatCodes.Length switch
        {
            0 => PgWireProtocol.FormatText,   // no codes: everything is text
            1 => formatCodes[0],              // one code applies to all values
            _ => formatCodes[index],          // per-value codes
        };

        /// <summary>
        /// Decodes a TEXT-format parameter to a CLR value typed by its pg OID, so a bound
        /// parameter behaves like the equivalent SQL literal when it reaches the filter. An
        /// unspecified OID (0) or an unmapped type stays a string. A value that does not parse
        /// for its declared type raises <see cref="FormatException"/> (→ clean bind error).
        /// </summary>
        private static object? DecodeTextParameter(int oid, byte[] raw)
        {
            var text = Encoding.UTF8.GetString(raw);
            return oid switch
            {
                PgTypeMap.OidBool => ParseBool(text),
                PgTypeMap.OidInt2 or PgTypeMap.OidInt4 or PgTypeMap.OidInt8
                    => long.Parse(text, CultureInfo.InvariantCulture),
                PgTypeMap.OidFloat4 or PgTypeMap.OidFloat8
                    => double.Parse(text, CultureInfo.InvariantCulture),
                PgTypeMap.OidNumeric => decimal.Parse(text, CultureInfo.InvariantCulture),
                PgTypeMap.OidUuid => Guid.Parse(text),
                PgTypeMap.OidDate or PgTypeMap.OidTimestamp
                    => DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.None),
                _ => text, // text/varchar/unspecified: keep the string
            };
        }

        private static bool ParseBool(string text) => text switch
        {
            "t" or "true" or "TRUE" or "1" or "yes" or "on" => true,
            "f" or "false" or "FALSE" or "0" or "no" or "off" => false,
            _ => throw new FormatException($"invalid boolean parameter '{text}'."),
        };

        private static bool IsQueryPhaseFault(Exception ex)
            => ex is not (IOException or OperationCanceledException or EndOfStreamException or PgProtocolException);

        private async Task FailFromExceptionAsync(Exception ex, CancellationToken ct)
        {
            var (sqlState, message) = PgQueryError.Map(ex);
            _logger.LogWarning(ex, "pgwire extended query failed ({SqlState}): {Message}", sqlState, ex.Message);
            await FailAsync(sqlState, message, ct);
        }

        /// <summary>Emits an ErrorResponse and enters skip-until-Sync, per the extended protocol.</summary>
        private async Task FailAsync(string sqlState, string message, CancellationToken ct)
        {
            IsSkipping = true;
            await PgProtocolIO.WriteMessageAsync(_stream, PgWireProtocol.ErrorResponse,
                PgBackend.ErrorResponse(sqlState, message, PgWireProtocol.SeverityError), ct);
        }

        /// <summary>
        /// Minimal big-endian reader over a message body with bounds checks. A plain (non-ref)
        /// struct over the body array so it can be used inside the async message handlers.
        /// </summary>
        private struct Reader
        {
            private readonly byte[] _body;
            private int _offset;
            public Reader(byte[] body) { _body = body; _offset = 0; }

            public byte ReadByte()
            {
                Require(1);
                return _body[_offset++];
            }

            public short ReadInt16()
            {
                Require(2);
                var v = BinaryPrimitives.ReadInt16BigEndian(_body.AsSpan(_offset, 2));
                _offset += 2;
                return v;
            }

            public int ReadInt32()
            {
                Require(4);
                var v = BinaryPrimitives.ReadInt32BigEndian(_body.AsSpan(_offset, 4));
                _offset += 4;
                return v;
            }

            public byte[] ReadBytes(int length)
            {
                if (length < 0) throw new PgProtocolException($"negative field length {length}.");
                Require(length);
                var slice = _body.AsSpan(_offset, length).ToArray();
                _offset += length;
                return slice;
            }

            public string ReadCString()
            {
                var end = _offset;
                while (end < _body.Length && _body[end] != 0) end++;
                if (end >= _body.Length) throw new PgProtocolException("unterminated C-string in message body.");
                var text = Encoding.UTF8.GetString(_body, _offset, end - _offset);
                _offset = end + 1;
                return text;
            }

            private readonly void Require(int count)
            {
                if (_offset + count > _body.Length)
                    throw new PgProtocolException("extended-protocol message body is truncated.");
            }
        }
    }

    /// <summary>
    /// A named or unnamed prepared statement: the raw SQL and the Parse-declared parameter
    /// type OIDs (may be shorter than the actual <c>$N</c> count, in which case the extra
    /// placeholders are inferred). Immutable — re-Parse replaces the map entry.
    /// </summary>
    internal sealed record PgPreparedStatement(string Sql, int[] ParameterTypeOids);

    /// <summary>
    /// A bound portal: the prepared statement plus this bind's parameter values, and the
    /// lazily-resolved plan + executed rows (with a resume position for row-limited Execute).
    /// A re-Bind to the same portal name creates a fresh instance, discarding cached rows.
    /// </summary>
    internal sealed class PgPortal
    {
        public PgPortal(PgPreparedStatement statement, object?[] parameters)
        {
            Statement = statement;
            Parameters = parameters;
        }

        public PgPreparedStatement Statement { get; }
        public object?[] Parameters { get; }

        /// <summary>Columns + row source, resolved once on first Describe/Execute.</summary>
        internal object? Resolved { get; set; }

        /// <summary>Materialized result rows, cached on first Execute for row-limited resume.</summary>
        public IReadOnlyList<IReadOnlyDictionary<string, object?>>? ExecutedRows { get; set; }

        /// <summary>Next row index to emit; advanced by each row-limited Execute.</summary>
        public int Position { get; set; }
    }
}
