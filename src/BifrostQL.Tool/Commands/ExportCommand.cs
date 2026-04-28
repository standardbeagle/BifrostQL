using System.Text.Json;
using BifrostQL.Core.Model;
using BifrostQL.Model;
using Microsoft.Data.SqlClient;

namespace BifrostQL.Tool.Commands;

/// <summary>
/// Exports database schema information in various formats.
/// </summary>
public sealed class ExportCommand : ICommand
{
    public string Name => "export";
    public string Description => "Export database schema to JSON, SQL, or Markdown";

    public async Task<int> ExecuteAsync(ToolConfig config, OutputFormatter output)
    {
        var connectionString = config.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            if (output.IsJsonMode)
            {
                output.WriteJson(new { success = false, error = "Connection string is required. Use --connection-string." });
                return 1;
            }
            output.WriteError("Connection string is required. Use --connection-string.");
            return 1;
        }

        // Determine format
        var format = "json";
        if (config.CommandArgs.Length > 0)
        {
            format = config.CommandArgs[0].ToLowerInvariant();
        }

        if (format != "json" && format != "sql" && format != "markdown" && format != "md")
        {
            if (output.IsJsonMode)
            {
                output.WriteJson(new { success = false, error = $"Unknown format: {format}. Use json, sql, or markdown." });
                return 1;
            }
            output.WriteError($"Unknown format: {format}");
            output.WriteInfo("Supported formats: json, sql, markdown");
            return 1;
        }

        try
        {
            var metadataLoader = new MetadataLoader(Array.Empty<string>());
            var loader = new DbModelLoader(connectionString, metadataLoader);
            var model = await loader.LoadAsync();

            var result = format switch
            {
                "json" => ExportJson(model, output),
                "sql" => ExportSql(model, output),
                "markdown" or "md" => ExportMarkdown(model, output),
                _ => throw new InvalidOperationException($"Unknown format: {format}")
            };

            return result;
        }
        catch (SqlException ex)
        {
            if (output.IsJsonMode)
            {
                output.WriteJson(new { success = false, error = ex.Message });
                return 1;
            }
            output.WriteError($"Export failed: {ex.Message}");
            return 1;
        }
    }

    private static int ExportJson(IDbModel model, OutputFormatter output)
    {
        var tables = model.Tables.Select(t =>
        {
            var dbTable = (DbTable)t;
            return new
            {
                schema = dbTable.TableSchema,
                name = dbTable.DbName,
                graphqlName = dbTable.GraphQlName,
                primaryKey = dbTable.KeyColumns.Select(c => c.ColumnName),
                columns = dbTable.Columns.Select(c => new
                {
                    name = c.ColumnName,
                    graphqlName = c.GraphQlName,
                    type = c.DataType,
                    isNullable = c.IsNullable,
                    isPrimaryKey = c.IsPrimaryKey,
                    isIdentity = c.IsIdentity,
                }),
                singleLinks = dbTable.SingleLinks.Select(l => new
                {
                    name = l.Key,
                    targetTable = l.Value.ChildTable.GraphQlName,
                }),
                multiLinks = dbTable.MultiLinks.Select(l => new
                {
                    name = l.Key,
                    targetTable = l.Value.ChildTable.GraphQlName,
                }),
            };
        });

        var export = new
        {
            exportedAt = DateTime.UtcNow,
            tableCount = model.Tables.Count,
            tables,
        };

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        var json = JsonSerializer.Serialize(export, options);
        output.WritePlain(json);
        return 0;
    }

    private static int ExportSql(IDbModel model, OutputFormatter output)
    {
        var sql = new System.Text.StringBuilder();
        sql.AppendLine("-- BifrostQL Schema Export");
        sql.AppendLine($"-- Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        sql.AppendLine($"-- Tables: {model.Tables.Count}");
        sql.AppendLine();

        foreach (var table in model.Tables.OrderBy(t => $"{((DbTable)t).TableSchema}.{((DbTable)t).DbName}"))
        {
            var dbTable = (DbTable)table;
            sql.AppendLine($"-- Table: {dbTable.TableSchema}.{dbTable.DbName}");
            sql.AppendLine($"CREATE TABLE [{dbTable.TableSchema}].[{dbTable.DbName}] (");
            
            var columns = dbTable.Columns.ToList();
            for (int i = 0; i < columns.Count; i++)
            {
                var col = columns[i];
                var nullable = col.IsNullable ? "NULL" : "NOT NULL";
                var pk = col.IsPrimaryKey ? " PRIMARY KEY" : "";
                var identity = col.IsIdentity ? " IDENTITY(1,1)" : "";
                var comma = i < columns.Count - 1 ? "," : "";
                
                sql.AppendLine($"    [{col.ColumnName}] {col.DataType}{identity} {nullable}{pk}{comma}");
            }
            
            sql.AppendLine(");");
            sql.AppendLine();
        }

        output.WritePlain(sql.ToString());
        return 0;
    }

    private static int ExportMarkdown(IDbModel model, OutputFormatter output)
    {
        var md = new System.Text.StringBuilder();
        md.AppendLine("# Database Schema");
        md.AppendLine();
        md.AppendLine($"**Exported:** {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC  ");
        md.AppendLine($"**Tables:** {model.Tables.Count}");
        md.AppendLine();
        md.AppendLine("## Table of Contents");
        md.AppendLine();

        foreach (var table in model.Tables.OrderBy(t => $"{((DbTable)t).TableSchema}.{((DbTable)t).DbName}"))
        {
            var dbTable = (DbTable)table;
            var anchor = $"{dbTable.TableSchema.ToLower()}-{dbTable.DbName.ToLower()}";
            md.AppendLine($"- [{dbTable.TableSchema}.{dbTable.DbName}](#{anchor})");
        }

        md.AppendLine();
        md.AppendLine("---");
        md.AppendLine();

        foreach (var table in model.Tables.OrderBy(t => $"{((DbTable)t).TableSchema}.{((DbTable)t).DbName}"))
        {
            var dbTable = (DbTable)table;
            md.AppendLine($"## {dbTable.TableSchema}.{dbTable.DbName}");
            md.AppendLine();
            md.AppendLine($"**GraphQL Name:** `{dbTable.GraphQlName}`  ");
            
            var keyCols = dbTable.KeyColumns.ToList();
            if (keyCols.Any())
            {
                md.AppendLine($"**Primary Key:** `{string.Join(", ", keyCols.Select(c => c.ColumnName))}`");
            }
            
            md.AppendLine();
            md.AppendLine("### Columns");
            md.AppendLine();
            md.AppendLine("| Column | GraphQL | Type | Nullable | PK | Identity |");
            md.AppendLine("|--------|---------|------|----------|----|----------|");

            foreach (var col in dbTable.Columns)
            {
                var nullable = col.IsNullable ? "Yes" : "No";
                var pk = col.IsPrimaryKey ? "✓" : "";
                var identity = col.IsIdentity ? "✓" : "";
                md.AppendLine($"| {col.ColumnName} | {col.GraphQlName} | {col.DataType} | {nullable} | {pk} | {identity} |");
            }

            md.AppendLine();

            if (dbTable.SingleLinks.Any())
            {
                md.AppendLine("### Single Links (Many-to-One)");
                md.AppendLine();
                md.AppendLine("| Name | Target Table |");
                md.AppendLine("|------|--------------|");

                foreach (var link in dbTable.SingleLinks)
                {
                    md.AppendLine($"| {link.Key} | {link.Value.ChildTable.GraphQlName} |");
                }

                md.AppendLine();
            }

            if (dbTable.MultiLinks.Any())
            {
                md.AppendLine("### Multi Links (One-to-Many)");
                md.AppendLine();
                md.AppendLine("| Name | Target Table |");
                md.AppendLine("|------|--------------|");

                foreach (var link in dbTable.MultiLinks)
                {
                    md.AppendLine($"| {link.Key} | {link.Value.ChildTable.GraphQlName} |");
                }

                md.AppendLine();
            }

            md.AppendLine();
        }

        output.WritePlain(md.ToString());
        return 0;
    }
}
