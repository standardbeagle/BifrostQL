using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;
using Microsoft.Extensions.Logging;

namespace BifrostQL.Core.Modules.Chat
{
    /// <summary>
    /// The built-in write-gating connector: every <c>chat-connector: plan</c> table
    /// becomes one tool per operation in its <c>chat-plan-operations</c> allow-list
    /// (<c>plan_insert_&lt;table&gt;</c>, <c>plan_update_&lt;table&gt;</c>,
    /// <c>plan_delete_&lt;table&gt;</c>) — a disallowed operation's tool does not
    /// exist in the schema at all.
    ///
    /// THE SECURITY CONTRACT (epic decision D2, no deviations):
    /// <list type="bullet">
    /// <item>A plan tool call NEVER writes. Execution validates the rows and returns
    /// a WRITE PROPOSAL (<see cref="ChatToolResult.ConfirmationRequest"/>) the tool
    /// loop parks on while the transport asks the user.</item>
    /// <item>A confirmed proposal executes through
    /// <see cref="IMutationIntentExecutor.ExecuteBatchAsync"/> under the ORIGINAL
    /// caller's auth context: the full mutation transformer chain (tenant stamp,
    /// policy, validation, audit, encryption) and the in-transaction hooks (history
    /// trail, CDC outbox) apply by construction, and ALL rows commit in ONE
    /// transaction — a veto anywhere writes nothing.</item>
    /// <item>A denied (or timed-out) proposal feeds a declined, non-error tool
    /// result back and the model continues. Nothing is written.</item>
    /// <item>The confirmation id is single-use, cryptographically random, and bound
    /// to the requesting identity and conversation
    /// (<see cref="ChatPlanConfirmationRegistry"/>); it expires with the request.</item>
    /// </list>
    ///
    /// Input surface: <c>visibility: hidden</c> columns are absent everywhere
    /// (schema, resolution, and error feedback), database-generated columns
    /// (identity, computed) are not writable, and encrypted columns ARE writable —
    /// the encrypt-on-write transformer handles them like any other write path.
    /// Validation is model-visible first: unknown columns, missing primary keys,
    /// empty or over-cap row sets throw <see cref="ChatToolInputException"/> naming
    /// the valid choices so the model can recover.
    /// </summary>
    public sealed class PlanChatConnector : IChatConnector
    {
        /// <summary>
        /// The tool-name prefix, namespacing plan tools against the explore_/media_
        /// tools of the other connector slices.
        /// </summary>
        public const string ToolNamePrefix = "plan_";

        private readonly IQueryIntentExecutor _reads;
        private readonly IMutationIntentExecutor _writes;
        private readonly ChatPlanConfirmationRegistry _confirmations;
        private readonly ChatConnectorOptions _options;
        private readonly ILogger _logger;
        private readonly string? _endpoint;

        public PlanChatConnector(
            IQueryIntentExecutor reads,
            IMutationIntentExecutor writes,
            ChatPlanConfirmationRegistry confirmations,
            ChatConnectorOptions? options = null,
            string? endpoint = null,
            ILogger<PlanChatConnector>? logger = null)
        {
            _reads = reads ?? throw new ArgumentNullException(nameof(reads));
            _writes = writes ?? throw new ArgumentNullException(nameof(writes));
            _confirmations = confirmations ?? throw new ArgumentNullException(nameof(confirmations));
            _options = options ?? new ChatConnectorOptions();
            _endpoint = endpoint;
            _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<PlanChatConnector>.Instance;
        }

        /// <inheritdoc />
        public int Priority => 120;

        /// <summary>The plan tool name for one allowed operation: <c>plan_&lt;operation&gt;_&lt;GraphQL name&gt;</c>.</summary>
        public static string ToolName(IDbTable table, MutationType operation) =>
            $"{ToolNamePrefix}{OperationToken(operation)}_{table.GraphQlName}";

        private static string OperationToken(MutationType operation) => operation.ToString().ToLowerInvariant();

        /// <inheritdoc />
        public IReadOnlyList<ChatToolDefinition> GetToolDefinitions(IDbModel model, ChatConnectorBinding binding)
        {
            if (binding is null)
                throw new ArgumentNullException(nameof(binding));
            if (!binding.Config.Plan)
                return Array.Empty<ChatToolDefinition>();

            var table = RequireServableTable(binding);
            // Only allow-listed operations generate tools: a disallowed operation is
            // ABSENT from the schema, not present-and-refused.
            return binding.Config.PlanOperations
                .Select(operation => new ChatToolDefinition(
                    ToolName(table, operation),
                    BuildDescription(table, binding.Config, operation),
                    BuildInputSchema(table, operation))
                {
                    RequiresConfirmation = true,
                })
                .ToList();
        }

        /// <inheritdoc />
        public async Task<ChatToolResult> ExecuteAsync(
            string toolName,
            string inputJson,
            IDictionary<string, object?> authContext,
            CancellationToken cancellationToken)
        {
            if (authContext is null)
                throw new ArgumentNullException(nameof(authContext));

            var model = await _reads.GetModelAsync(_endpoint);
            var (binding, operation) = ResolvePlanTool(model, toolName);
            var table = RequireServableTable(binding);
            var rows = ParseRows(table, operation, inputJson ?? "{}");

            // Identity + conversation binding BEFORE registering anything: a transport
            // that cannot bind them cannot host plan tools (configuration fault).
            var identityKey = ChatPlanConfirmationRegistry.RequireIdentityKey(authContext);
            var conversationKey = ChatPlanConfirmationRegistry.RequireConversationKey(authContext);

            var pending = _confirmations.Register(
                identityKey, conversationKey, _options.PlanConfirmationTimeout, cancellationToken);
            var summary = BuildSummary(table, operation, rows.Count);

            // NO write happened and none will until the user approves: the tool loop
            // parks on ResolveAsync between provider turns (the current turn's stream
            // is already terminal when tools execute, so no HTTP request is held open).
            return new ChatToolResult
            {
                TextPayload = new JsonObject
                {
                    ["status"] = "pending-confirmation",
                    ["confirmationId"] = pending.ConfirmationId,
                    ["summary"] = summary,
                }.ToJsonString(),
                ConfirmationRequest = new ChatToolConfirmationRequest
                {
                    ConfirmationId = pending.ConfirmationId,
                    Table = table.GraphQlName,
                    Operation = OperationToken(operation),
                    Rows = rows.Select(r => (IReadOnlyDictionary<string, object?>)r.Display).ToList(),
                    Summary = summary,
                    ResolveAsync = ct => ResolveProposalAsync(
                        toolName, table, operation, rows, authContext, pending, ct),
                },
            };
        }

        // ---- proposal resolution (the ONLY path that writes) -------------------------

        private async Task<ChatToolConfirmationOutcome> ResolveProposalAsync(
            string toolName,
            IDbTable table,
            MutationType operation,
            IReadOnlyList<ProposedRow> rows,
            IDictionary<string, object?> authContext,
            PendingPlanConfirmation pending,
            CancellationToken cancellationToken)
        {
            // Park. The decision task resolves on confirm/deny, denies itself on
            // timeout, and cancels with the request (teardown propagates: no write,
            // no result, the id is already dead).
            var decision = await pending.Decision.WaitAsync(cancellationToken).ConfigureAwait(false);

            if (!decision.Approved)
            {
                // Declined is NOT an error: the model reads the outcome and continues
                // (e.g. revises the proposal per the user's reason).
                var declined = new JsonObject
                {
                    ["approved"] = false,
                    ["reason"] = decision.Reason ?? "The user declined the proposal.",
                };
                return new ChatToolConfirmationOutcome(false, decision.Reason,
                    new ChatToolResult { TextPayload = declined.ToJsonString() });
            }

            try
            {
                // One transaction for ALL rows through the batch intent seam: the full
                // transformer chain and hooks run per row, and a veto on ANY row rolls
                // back the whole proposal — no partial batch.
                var result = await _writes.ExecuteBatchAsync(new MutationBatchIntent
                {
                    Table = table.DbName,
                    Actions = rows
                        .Select(r => new MutationBatchAction(ToIntentAction(operation), r.Data))
                        .ToList(),
                    UserContext = authContext,
                    Endpoint = _endpoint,
                }, cancellationToken).ConfigureAwait(false);

                var approved = new JsonObject
                {
                    ["approved"] = true,
                    ["affected"] = result.TotalAffected,
                };
                return new ChatToolConfirmationOutcome(true, decision.Reason,
                    new ChatToolResult { TextPayload = approved.ToJsonString() });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A transformer veto (or any execution failure) after approval: the
                // batch transaction rolled back — ZERO rows landed — and the model
                // sees a SANITIZED is_error result (raw pipeline messages can carry
                // server-side detail; the full exception stays in the server log).
                _logger.LogError(ex,
                    "Plan tool {ToolName} confirmed write was rejected; nothing was written and a sanitized " +
                    "error was fed back to the model.", toolName);
                return new ChatToolConfirmationOutcome(true, decision.Reason, new ChatToolResult
                {
                    TextPayload = $"Tool '{toolName}' failed: {ex.GetType().Name}. " +
                        "The approved write was rejected by the server and NO rows were written.",
                    IsError = true,
                });
            }
        }

        private static MutationIntentAction ToIntentAction(MutationType operation) => operation switch
        {
            MutationType.Insert => MutationIntentAction.Insert,
            MutationType.Update => MutationIntentAction.Update,
            MutationType.Delete => MutationIntentAction.Delete,
            _ => throw new InvalidOperationException($"Unsupported plan operation '{operation}'."),
        };

        // ---- binding rules (fail-fast configuration faults) --------------------------

        // ModelConfigValidator reports these at model load; re-asserting keeps the
        // connector safe against models built without validation.
        private static IDbTable RequireServableTable(ChatConnectorBinding binding)
        {
            var table = binding.Table;
            if (!table.KeyColumns.Any())
                throw new InvalidOperationException(
                    $"The plan connector table {table.TableSchema}.{table.DbName} has no primary-key column; " +
                    "plan updates and deletes cannot address rows.");
            if (table.KeyColumns.Any(IsHidden))
                throw new InvalidOperationException(
                    $"The plan connector table {table.TableSchema}.{table.DbName} hides a primary-key column " +
                    $"({MetadataKeys.Ui.Visibility}: {MetadataKeys.Ui.Hidden}); plan tools cannot address rows " +
                    "without naming the key.");
            return table;
        }

        private static bool IsHidden(ColumnDto column) =>
            column.CompareMetadata(MetadataKeys.Ui.Visibility, MetadataKeys.Ui.Hidden);

        // ---- column surfaces ----------------------------------------------------------

        /// <summary>Columns a proposal may write: visible and not database-generated. Encrypted columns stay (encrypt-on-write handles them).</summary>
        private static IEnumerable<ColumnDto> WritableColumns(IDbTable table) =>
            ConnectorQuerySchema.VisibleColumns(table).Where(c => !c.IsIdentity && !c.IsComputed);

        private static IEnumerable<ColumnDto> SetColumns(IDbTable table) =>
            WritableColumns(table).Where(c => !c.IsPrimaryKey);

        private static List<ColumnDto> KeyColumns(IDbTable table) => table.KeyColumns.ToList();

        // ---- tool definitions -----------------------------------------------------------

        private string BuildDescription(IDbTable table, ChatConnectorConfig config, MutationType operation)
        {
            var qualified = $"{table.TableSchema}.{table.DbName}";
            var action = operation switch
            {
                MutationType.Insert =>
                    $"Call this when the user wants NEW rows added to {table.GraphQlName} (table {qualified}).",
                MutationType.Update =>
                    $"Call this when the user wants EXISTING rows of {table.GraphQlName} (table {qualified}) " +
                    "changed; each row names its primary key plus only the columns to change.",
                _ =>
                    $"Call this when the user wants rows REMOVED from {table.GraphQlName} (table {qualified}); " +
                    "each row names only the primary key of one row to delete.",
            };
            var description =
                $"{action} This does NOT write anything: it creates a write proposal the user must approve, " +
                "and the tool result reports whether it was approved (and applied) or declined. " +
                $"Propose at most {_options.PlanRowCap} rows per call.";
            return config.ToolDescription is null ? description : $"{description} {config.ToolDescription}";
        }

        private string BuildInputSchema(IDbTable table, MutationType operation)
        {
            var (columns, required) = operation switch
            {
                MutationType.Insert => (WritableColumns(table).ToList(), new List<ColumnDto>()),
                MutationType.Update => (KeyColumns(table).Concat(SetColumns(table)).ToList(), KeyColumns(table)),
                _ => (KeyColumns(table), KeyColumns(table)),
            };

            var rowProperties = new JsonObject();
            foreach (var column in columns)
                rowProperties[column.GraphQlName] = ConnectorQuerySchema.ValueSchema(
                    ConnectorQuerySchema.Classify(column));

            var rowSchema = new JsonObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["properties"] = rowProperties,
            };
            if (required.Count > 0)
                rowSchema["required"] = new JsonArray(required.Select(c => (JsonNode)c.GraphQlName).ToArray());

            var schema = new JsonObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["properties"] = new JsonObject
                {
                    ["rows"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = rowSchema,
                        ["minItems"] = 1,
                        ["maxItems"] = _options.PlanRowCap,
                    },
                },
                ["required"] = new JsonArray("rows"),
            };
            return schema.ToJsonString();
        }

        // ---- input validation (the model is an untrusted caller) ----------------------

        /// <summary>One validated row: display keys are GraphQL names, execution keys are DB column names.</summary>
        private sealed record ProposedRow(
            Dictionary<string, object?> Display, Dictionary<string, object?> Data);

        private static (ChatConnectorBinding Binding, MutationType Operation) ResolvePlanTool(
            IDbModel model, string toolName)
        {
            foreach (var binding in ChatConnectorConfig.FromModel(model))
            {
                if (!binding.Config.Plan)
                    continue;
                // Only ALLOWED operations resolve: the tool for a disallowed operation
                // does not exist, so calling it is an unknown tool, not a refusal.
                foreach (var operation in binding.Config.PlanOperations)
                {
                    if (ToolName(binding.Table, operation) == toolName)
                        return (binding, operation);
                }
            }
            throw new ChatToolInputException(
                $"Unknown plan tool '{toolName}'; no plan-connector table generates it.");
        }

        private IReadOnlyList<ProposedRow> ParseRows(IDbTable table, MutationType operation, string inputJson)
        {
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(inputJson);
            }
            catch (JsonException ex)
            {
                throw new ChatToolInputException("Tool input must be a JSON object.", ex);
            }

            using (document)
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    throw new ChatToolInputException("Tool input must be a JSON object.");

                JsonElement? rowsElement = null;
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (property.Name == "rows")
                        rowsElement = property.Value;
                    else
                        throw new ChatToolInputException(
                            $"Unknown argument '{property.Name}'. The only valid argument is 'rows'.");
                }

                if (rowsElement is not { ValueKind: JsonValueKind.Array } rowsArray
                    || rowsArray.GetArrayLength() == 0)
                    throw new ChatToolInputException("'rows' must be a non-empty array of row objects.");
                if (rowsArray.GetArrayLength() > _options.PlanRowCap)
                    throw new ChatToolInputException(
                        $"'rows' proposes {rowsArray.GetArrayLength()} rows; a plan proposal is capped at " +
                        $"{_options.PlanRowCap} rows per call. Split the work into smaller proposals.");

                var rows = new List<ProposedRow>();
                foreach (var element in rowsArray.EnumerateArray())
                    rows.Add(ParseRow(table, operation, element));
                return rows;
            }
        }

        private static ProposedRow ParseRow(IDbTable table, MutationType operation, JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object)
                throw new ChatToolInputException("Each entry of 'rows' must be a row object.");

            var display = new Dictionary<string, object?>(StringComparer.Ordinal);
            var data = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                // Hidden columns resolve exactly like nonexistent ones, and the
                // valid-columns feedback discloses only visible names.
                var column = ConnectorQuerySchema.ResolveColumn(table, property.Name, "rows");
                GuardColumnForOperation(table, operation, column);
                var value = RowValue(column, property.Value);
                display[column.GraphQlName] = value;
                data[column.ColumnName] = value;
            }

            RequireRowShape(table, operation, data);
            return new ProposedRow(display, data);
        }

        private static void GuardColumnForOperation(IDbTable table, MutationType operation, ColumnDto column)
        {
            if (operation == MutationType.Delete)
            {
                if (!column.IsPrimaryKey)
                    throw new ChatToolInputException(
                        $"Column '{column.GraphQlName}' is not valid in a delete row; name only the primary key " +
                        $"column(s): {KeyColumnNames(table)}.");
                return;
            }

            // A primary key in an update row ADDRESSES the row (identity keys
            // included); everywhere else a database-generated column is not writable.
            if (operation == MutationType.Update && column.IsPrimaryKey)
                return;

            if (column.IsIdentity || column.IsComputed)
                throw new ChatToolInputException(
                    $"Column '{column.GraphQlName}' cannot be written; it is generated by the database.");
        }

        private static void RequireRowShape(
            IDbTable table, MutationType operation, Dictionary<string, object?> data)
        {
            if (data.Count == 0)
                throw new ChatToolInputException("A row object must name at least one column.");

            if (operation is MutationType.Update or MutationType.Delete)
            {
                var missing = table.KeyColumns.Where(k => !data.ContainsKey(k.ColumnName)).ToList();
                if (missing.Count > 0)
                    throw new ChatToolInputException(
                        $"Each {OperationToken(operation)} row must include the primary key column(s): " +
                        $"{KeyColumnNames(table)}.");
            }

            if (operation == MutationType.Update
                && table.KeyColumns.Count() == data.Count)
                throw new ChatToolInputException(
                    "An update row names only the primary key; include at least one column to change.");
        }

        private static string KeyColumnNames(IDbTable table) =>
            string.Join(", ", table.KeyColumns.Select(c => c.GraphQlName));

        private static object? RowValue(ColumnDto column, JsonElement value) => value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.TryGetInt64(out var integer) ? integer : value.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => throw new ChatToolInputException(
                $"The value for column '{column.GraphQlName}' must be a JSON scalar (string, number, boolean, or null)."),
        };

        private static string BuildSummary(IDbTable table, MutationType operation, int rowCount)
        {
            var rows = rowCount == 1 ? "1 row" : $"{rowCount} rows";
            return operation switch
            {
                MutationType.Insert => $"insert {rows} into {table.TableSchema}.{table.DbName}",
                MutationType.Update => $"update {rows} in {table.TableSchema}.{table.DbName}",
                _ => $"delete {rows} from {table.TableSchema}.{table.DbName}",
            };
        }
    }
}
