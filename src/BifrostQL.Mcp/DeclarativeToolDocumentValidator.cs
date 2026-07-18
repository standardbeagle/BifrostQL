using System.Text.Json;
using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;
using Microsoft.Extensions.Hosting;

namespace BifrostQL.Mcp
{
    public sealed class DeclarativeToolDocumentValidator(
        DeclarativeToolDocument document,
        IQueryIntentExecutor executor) : IHostedService
    {
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            var model = await executor.GetModelAsync();
            var errors = Validate(document, model);
            if (errors.Count != 0)
                throw new InvalidOperationException(
                    "Declarative MCP tool document validation failed:" + Environment.NewLine +
                    string.Join(Environment.NewLine, errors.Select(error => $"- {error}")));
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public static IReadOnlyList<string> Validate(DeclarativeToolDocument document, IDbModel model)
        {
            var errors = new List<string>();
            foreach (var tool in document.Tools)
                ValidateTool(tool, model, errors);
            return errors;
        }

        private static void ValidateTool(DeclarativeToolDefinition tool, IDbModel model, List<string> errors)
        {
            var prefix = $"Tool '{tool.Name}'";
            foreach (var (name, parameter) in tool.Params)
            {
                if (parameter.Type == "id" && parameter.Table is { } parameterTable &&
                    FindTable(model, parameterTable) is null)
                    errors.Add($"{prefix} at params.{name}.table references unknown table '{parameterTable}'.");
                if (parameter.Type == "enum" && parameter.Default is { } defaultValue && parameter.Values is { } values &&
                    (defaultValue.ValueKind != JsonValueKind.String || !values.Contains(defaultValue.GetString()!, StringComparer.Ordinal)))
                    errors.Add($"{prefix} at params.{name}.default references value '{defaultValue}' outside enum values.");
            }

            if (!tool.Params.ContainsKey(tool.Root.ById))
                errors.Add($"{prefix} at root.byId references undeclared parameter '{tool.Root.ById}'.");

            var table = FindTable(model, tool.Root.Table);
            if (table is null)
            {
                errors.Add($"{prefix} at root.table references unknown table '{tool.Root.Table}'.");
                return;
            }

            foreach (var field in tool.Root.Fields)
                CheckColumn(table, field, $"{prefix} at root.fields", errors);

            for (var index = 0; index < tool.Include.Count; index++)
            {
                var include = tool.Include[index];
                var path = $"{prefix} at include[{index}]";
                var related = ResolveRelatedTable(table, include.Relation);
                if (related is null)
                {
                    errors.Add($"{path}.relation references unknown relation '{include.Relation}'.");
                    continue;
                }

                foreach (var field in include.Fields ?? [])
                    CheckColumn(related, field, $"{path}.fields", errors);
                if (include.Filter is { ValueKind: JsonValueKind.Object } filter)
                    ValidateFilterColumns(filter, related, $"{path}.filter", errors);
                if (include.Aggregate is { } aggregate)
                {
                    foreach (var (operation, column) in new[]
                    {
                        ("sum", aggregate.Sum), ("avg", aggregate.Avg),
                        ("min", aggregate.Min), ("max", aggregate.Max),
                    })
                    {
                        if (column is not null)
                            CheckColumn(related, column, $"{path}.aggregate.{operation}", errors);
                    }
                }
            }
        }

        private static IDbTable? FindTable(IDbModel model, string qualifiedName) =>
            model.Tables.FirstOrDefault(candidate =>
                string.Equals($"{candidate.TableSchema}.{candidate.DbName}", qualifiedName, StringComparison.OrdinalIgnoreCase));

        private static IDbTable? ResolveRelatedTable(IDbTable table, string relation)
        {
            if (table.SingleLinks.TryGetValue(relation, out var single) || table.MultiLinks.TryGetValue(relation, out single))
                return ReferenceEquals(single.ParentTable, table) ? single.ChildTable : single.ParentTable;
            return table.ManyToManyLinks.TryGetValue(relation, out var many) ? many.TargetTable : null;
        }

        private static void ValidateFilterColumns(JsonElement filter, IDbTable table, string path, List<string> errors)
        {
            foreach (var property in filter.EnumerateObject())
            {
                if (property.Name is "and" or "or" && property.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var child in property.Value.EnumerateArray())
                        if (child.ValueKind == JsonValueKind.Object)
                            ValidateFilterColumns(child, table, path, errors);
                    continue;
                }
                CheckColumn(table, property.Name, path, errors);
            }
        }

        private static void CheckColumn(IDbTable table, string column, string path, List<string> errors)
        {
            if (!table.ColumnLookup.ContainsKey(column) && !table.GraphQlLookup.ContainsKey(column))
                errors.Add($"{path} references unknown column '{column}' on table '{table.TableSchema}.{table.DbName}'.");
        }
    }
}
