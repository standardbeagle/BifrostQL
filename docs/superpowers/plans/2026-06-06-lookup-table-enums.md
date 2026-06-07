# Lookup-table Enums Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Wire lookup tables annotated with `enum:` metadata into the GraphQL pipeline so they emit real enum types, referencing columns are typed/filterable/writable as those enums, and reads/filters/writes translate between the enum name and the stored DB value.

**Architecture:** A schema-build-time loader (`EnumValueLoader`) reads + sanitizes distinct lookup values through the normal read pipeline (security applies). An `EnumColumnMap` resolves which columns are enums (FK-targets-value-column or `enum-ref:` override) and does `value↔name` translation. The map is cached on `DbModel` (alongside `EavConfigs`). `SchemaGenerator`/`TableSchemaGenerator` emit enum + filter-input types and render enum columns. Runtime translation happens in three pinned spots: `ReaderEnum.Get` (read value→name, null+warning on miss), a filter rewrite walked over `GqlObjectQuery.Filter` before SQL generation (name→value), and a mutation transformer (name→value on write).

**Tech Stack:** C# / .NET (net8/9/10), xUnit + FluentAssertions + NSubstitute, Microsoft.Data.SqlClient/Npgsql/MySqlConnector/Microsoft.Data.Sqlite. Integration tests run against SqlServer/Postgres/MySQL/SQLite via the `DatabaseFixture<T>` + `BIFROST_TEST_*` env vars.

**Approach A scope:** value-valued columns only. Columns hold the value string; map is `value ↔ sanitized-name`. FK-by-id enums are out of scope (separate follow-up).

---

## Existing types this plan depends on (already in the codebase — do not redefine)

- `EnumTableConfig` (`src/BifrostQL.Core/Schema/EnumTableConfig.cs`): `FromTable(IDbTable) → EnumTableConfig?`; props `TableName`, `ValueColumn` (nullable = auto), `LabelColumn`, `GraphQlEnumName` (`{table.GraphQlName}Values`); `const string MetadataKey = "enum"`.
- `EnumValueSanitizer` (`src/BifrostQL.Core/Schema/EnumValueSanitizer.cs`): `static string? Sanitize(string?)`, `static IReadOnlyList<EnumValueEntry> SanitizeAll(IEnumerable<string?>)`.
- `EnumValueEntry(string GraphQlName, string DatabaseValue)` — record in the same file.
- `EnumTableSchemaGenerator` (`src/BifrostQL.Core/Schema/EnumTableSchemaGenerator.cs`): ctor `(EnumTableConfig config, IReadOnlyList<EnumValueEntry> values)`; `string GraphQlEnumName`; `string GenerateEnumTypeDefinition()` (emits `enum {Name} {...}` or `""`); `string GetFilterInputTypeName()` (`FilterType{Name}Input`); `string GetFilterTypeDefinition()`.
- `ColumnDto` (`src/BifrostQL.Core/Model/ColumnDto.cs`): `TableName`, `ColumnName`, `GraphQlName`, `DataType`, `EffectiveDataType`, `IsNullable`, `IsPrimaryKey`, `IDictionary<string,object?> Metadata`, `string? GetMetadataValue(string)`.
- `IDbTable`/`DbTable` (`src/BifrostQL.Core/Model/DbModel.cs`): `string DbName`, `string GraphQlName`, `IEnumerable<ColumnDto> Columns`, `IDictionary<string,ColumnDto> ColumnLookup`, `IDictionary<string,object?> Metadata`.
- `DbForeignKey` (`src/BifrostQL.Core/Model/DbForeignKey.cs`): `ParentTableName`, `IReadOnlyList<string> ParentColumnNames`, `ChildTableName`, `IReadOnlyList<string> ChildColumnNames`.
- `IDbModel`/`DbModel` (`src/BifrostQL.Core/Model/DbModel.cs`): `IReadOnlyCollection<IDbTable> Tables`; settable runtime props already present: `ITypeMapper TypeMapper { get; set; }`, `IReadOnlyList<EavConfig> EavConfigs { get; set; }`.
- `TableFilter` (`src/BifrostQL.Core/QueryModel/TableFilter.cs`): `string ColumnName`, `string RelationName`, `object? Value` (settable), `TableFilter? Next`, `List<TableFilter> And`, `List<TableFilter> Or`.
- `ReaderEnum` (`src/BifrostQL.Core/Resolvers/ReaderEnum.cs`): `ValueTask<object?> Get(int row, IBifrostFieldContext context)` — the read-projection point.
- `SqlExecutionManager` (`src/BifrostQL.Core/Resolvers/SqlExecutionManager.cs`): builds the query, runs `_transformerService.ApplyTransformers`, then `LoadDataParameterizedAsync`, then returns a `ReaderEnum`. Holds `_dbModel`.
- `DbModelLoader` (`src/BifrostQL.Core/Model/DbModelLoader.cs`): has `IDbConnFactory _connFactory`; `Task<SchemaData> ReadAsync()`.
- `ProfileModelCache` (`src/BifrostQL.Core/Schema/ProfileModelCache.cs`): caches the shared read + builds model+schema per profile.
- `SchemaGenerator.SchemaTextFromModel(IDbModel model, bool includeDynamicJoins)` (`src/BifrostQL.Core/Schema/SchemaGenerator.cs`), called from `DbSchema.cs:17`.
- `MetadataKeys` (`src/BifrostQL.Core/Model/MetadataKeys.cs`).
- `IMutationTransformer` / `MutationTransformContext` (has `IDbModel Model`, `IDictionary<string,object?> UserContext`) (`src/BifrostQL.Core/Modules/IMutationTransformer.cs`).

---

## File structure

| File | Responsibility |
|---|---|
| `src/BifrostQL.Core/Model/MetadataKeys.cs` (modify) | Add `Enum.Ref = "enum-ref"` constant. |
| `src/BifrostQL.Core/Schema/EnumColumnMap.cs` (create) | Column→enum resolution; `value↔name`; filter-tree rewrite. Pure, no I/O. |
| `src/BifrostQL.Core/Schema/EnumValueLoader.cs` (create) | Load + sanitize distinct lookup values per enum table (security pipeline). |
| `src/BifrostQL.Core/Model/DbModel.cs` (modify) | Add `EnumColumnMap? EnumColumns { get; set; }` to `IDbModel`/`DbModel`. |
| `src/BifrostQL.Core/Model/DbModelLoader.cs` (modify) | `LoadEnumValuesAsync(IDbModel)` → the values map. |
| `src/BifrostQL.Core/Schema/ProfileModelCache.cs` (modify) | Cache values map; attach `EnumColumnMap` to each built model. |
| `src/BifrostQL.Core/Schema/SchemaGenerator.cs` (modify) | Emit enum + filter-input types from the map. |
| `src/BifrostQL.Core/Schema/TableSchemaGenerator.cs` (modify) | Render enum columns as enum type + enum filter input. |
| `src/BifrostQL.Core/Resolvers/ReaderEnum.cs` (modify) | Read value→name with null+warning on miss. |
| `src/BifrostQL.Core/Modules/EnumValueMutationTransformer.cs` (create) | Write: rewrite enum input values name→value. |
| `src/BifrostQL.Core/Resolvers/SqlExecutionManager.cs` (modify) | Call `EnumColumns.RewriteFilterValues` on the query filter before SQL. |
| tests under `tests/BifrostQL.Core.Test/...` and `tests/BifrostQL.Integration.Test/...` | Unit, schema-snapshot, 4-DB integration. |

---

## Task 1: `enum-ref` metadata key constant

**Files:**
- Modify: `src/BifrostQL.Core/Model/MetadataKeys.cs`
- Test: `tests/BifrostQL.Core.Test/Unit/Model/MetadataKeysTests.cs` (create if absent)

- [ ] **Step 1: Write the failing test**

```csharp
using BifrostQL.Core.Model;
using FluentAssertions;
using Xunit;

public class EnumMetadataKeyTests
{
    [Fact]
    public void EnumRef_Key_HasExpectedValue()
    {
        MetadataKeys.Enum.Ref.Should().Be("enum-ref");
    }
}
```

- [ ] **Step 2: Run test, verify it fails**

Run: `dotnet test tests/BifrostQL.Core.Test --framework net10.0 --filter "FullyQualifiedName~EnumRef_Key_HasExpectedValue"`
Expected: FAIL — `MetadataKeys.Enum` does not exist.

- [ ] **Step 3: Add the constant**

In `MetadataKeys.cs`, add a nested static class (match the file's existing nested-class style):

```csharp
public static class Enum
{
    /// <summary>Column metadata: forces the column to render as a lookup-table enum, e.g. "enum-ref: dbo.status".</summary>
    public const string Ref = "enum-ref";
}
```

- [ ] **Step 4: Run test, verify it passes**

Run: same as Step 2. Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/BifrostQL.Core/Model/MetadataKeys.cs tests/BifrostQL.Core.Test/Unit/Model/MetadataKeysTests.cs
git commit -m "feat(enum): add enum-ref column metadata key"
```

---

## Task 2: `EnumColumnMap` — resolution + value↔name + filter rewrite

`EnumColumnMap` is pure (no DB). It is built from the model plus a precomputed `tableDbName → entries` map and a `tableDbName → resolvedValueColumn` map (both produced later by `EnumValueLoader`). It answers: is `(table,column)` an enum, and translate both directions; and rewrites a `TableFilter` tree in place.

**Files:**
- Create: `src/BifrostQL.Core/Schema/EnumColumnMap.cs`
- Test: `tests/BifrostQL.Core.Test/Unit/Schema/EnumColumnMapTests.cs`

- [ ] **Step 1: Write failing tests**

```csharp
using System.Collections.Generic;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Schema;
using FluentAssertions;
using Xunit;

public class EnumColumnMapTests
{
    // Model: table "orders" has column "status" that is an FK to lookup "status"(code);
    // lookup "status" is enum:code with values active/inactive -> ACTIVE/INACTIVE.
    private static (IDbModel model,
                    IReadOnlyDictionary<string, IReadOnlyList<EnumValueEntry>> values,
                    IReadOnlyDictionary<string, string> valueColumns) Fixture()
    {
        var statusTable = TestModel.Table("status", graphQl: "status",
            cols: new[] { TestModel.Col("status", "id", isPk: true), TestModel.Col("status", "code") },
            metadata: new() { ["enum"] = "code" });
        var ordersStatus = TestModel.Col("orders", "status");
        var ordersTable = TestModel.Table("orders", graphQl: "orders",
            cols: new[] { TestModel.Col("orders", "id", isPk: true), ordersStatus });
        var fk = new DbForeignKey
        {
            ChildTableSchema = "dbo", ChildTableName = "orders", ChildColumnNames = new[] { "status" },
            ParentTableSchema = "dbo", ParentTableName = "status", ParentColumnNames = new[] { "code" },
        };
        var model = TestModel.Model(new[] { statusTable, ordersTable }, new[] { fk });
        var values = new Dictionary<string, IReadOnlyList<EnumValueEntry>>
        {
            ["status"] = new[] { new EnumValueEntry("ACTIVE", "active"), new EnumValueEntry("INACTIVE", "inactive") }
        };
        var valueColumns = new Dictionary<string, string> { ["status"] = "code" };
        return (model, values, valueColumns);
    }

    [Fact]
    public void FkTargetingValueColumn_IsDetectedAsEnum()
    {
        var (model, values, valueCols) = Fixture();
        var map = EnumColumnMap.Build(model, values, valueCols);
        map.TryGetEnumType("orders", "status", out var enumName).Should().BeTrue();
        enumName.Should().Be("statusValues");
    }

    [Fact]
    public void MetadataRef_OverridesAndForcesEnum()
    {
        var (model, values, valueCols) = Fixture();
        // A column with no FK but enum-ref metadata.
        var notes = TestModel.Col("orders", "note_status", metadata: new() { ["enum-ref"] = "dbo.status" });
        model = TestModel.AddColumn(model, "orders", notes);
        var map = EnumColumnMap.Build(model, values, valueCols);
        map.TryGetEnumType("orders", "note_status", out var enumName).Should().BeTrue();
        enumName.Should().Be("statusValues");
    }

    [Fact]
    public void NonEnumColumn_IsNotDetected()
    {
        var (model, values, valueCols) = Fixture();
        EnumColumnMap.Build(model, values, valueCols)
            .TryGetEnumType("orders", "id", out _).Should().BeFalse();
    }

    [Fact]
    public void ValueToName_And_NameToValue_RoundTrip()
    {
        var (model, values, valueCols) = Fixture();
        var map = EnumColumnMap.Build(model, values, valueCols);
        map.ValueToName("orders", "status", "active").Should().Be("ACTIVE");
        map.NameToValue("orders", "status", "ACTIVE").Should().Be("active");
        map.ValueToName("orders", "status", "missing").Should().BeNull();   // drift
        map.NameToValue("orders", "status", "BOGUS").Should().BeNull();
    }

    [Fact]
    public void RewriteFilterValues_TranslatesEnumColumnOperands_Recursively()
    {
        var (model, values, valueCols) = Fixture();
        var map = EnumColumnMap.Build(model, values, valueCols);
        var filter = new TableFilter
        {
            ColumnName = "status", RelationName = "_eq", Value = "ACTIVE",
            FilterType = FilterType.Column,
            And = { new TableFilter { ColumnName = "status", RelationName = "_in",
                                      Value = new List<object?> { "ACTIVE", "INACTIVE" }, FilterType = FilterType.Column } }
        };
        map.RewriteFilterValues(filter, "orders");
        filter.Value.Should().Be("active");
        ((List<object?>)filter.And[0].Value!).Should().Equal("active", "inactive");
    }
}
```

> NOTE: `TestModel` is a small test helper. If an equivalent helper already exists in the test project, use it and delete the references above that don't match; otherwise add `tests/BifrostQL.Core.Test/Unit/Schema/TestModel.cs` with `Col`, `Table`, `Model`, `AddColumn` factory methods that build real `ColumnDto`/`DbTable`/`DbModel` instances. Build these against the actual constructors in `src/BifrostQL.Core/Model/DbModel.cs` and `ColumnDto.cs`.

- [ ] **Step 2: Run tests, verify they fail**

Run: `dotnet test tests/BifrostQL.Core.Test --framework net10.0 --filter "FullyQualifiedName~EnumColumnMapTests"`
Expected: FAIL — `EnumColumnMap` does not exist.

- [ ] **Step 3: Implement `EnumColumnMap`**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;

namespace BifrostQL.Core.Schema
{
    /// <summary>
    /// Resolves which table columns render as lookup-table enums and translates
    /// between the GraphQL enum name and the stored database value. Pure: built
    /// once per model from pre-loaded, sanitized enum values. (Approach A — the
    /// referencing column holds the value string; no id translation.)
    /// </summary>
    public sealed class EnumColumnMap
    {
        private sealed record ColumnEnum(
            string EnumName,
            IReadOnlyDictionary<string, string> ValueToName,   // dbValue -> graphQlName
            IReadOnlyDictionary<string, string> NameToValue);  // graphQlName -> dbValue

        // key: (tableDbName, columnDbName) case-insensitive
        private readonly Dictionary<(string Table, string Column), ColumnEnum> _columns;

        private EnumColumnMap(Dictionary<(string, string), ColumnEnum> columns) => _columns = columns;

        public static EnumColumnMap Build(
            IDbModel model,
            IReadOnlyDictionary<string, IReadOnlyList<EnumValueEntry>> enumValues,
            IReadOnlyDictionary<string, string> resolvedValueColumns)
        {
            var byTable = new Dictionary<string, ColumnEnum>(StringComparer.OrdinalIgnoreCase);
            foreach (var table in model.Tables)
            {
                var cfg = EnumTableConfig.FromTable(table);
                if (cfg == null) continue;
                if (!enumValues.TryGetValue(table.DbName, out var entries) || entries.Count == 0) continue;
                var v2n = new Dictionary<string, string>(StringComparer.Ordinal);
                var n2v = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var e in entries) { v2n[e.DatabaseValue] = e.GraphQlName; n2v[e.GraphQlName] = e.DatabaseValue; }
                byTable[table.DbName] = new ColumnEnum(cfg.GraphQlEnumName, v2n, n2v);
            }

            var columns = new Dictionary<(string, string), ColumnEnum>(
                comparer: System.Collections.Generic.EqualityComparer<(string, string)>.Default);
            var ci = StringComparer.OrdinalIgnoreCase;

            // Index FKs by (childTable, childColumn) -> (parentTable, parentColumn)
            var fkTargets = new Dictionary<(string, string), (string Table, string Column)>();
            foreach (var fk in EnumColumnMapForeignKeys(model))
                for (var i = 0; i < fk.ChildColumnNames.Count && i < fk.ParentColumnNames.Count; i++)
                    fkTargets[(fk.ChildTableName.ToLowerInvariant(), fk.ChildColumnNames[i].ToLowerInvariant())]
                        = (fk.ParentTableName, fk.ParentColumnNames[i]);

            foreach (var table in model.Tables)
            {
                foreach (var col in table.Columns)
                {
                    // 1) explicit override wins
                    var refMeta = col.GetMetadataValue(MetadataKeys.Enum.Ref);
                    if (!string.IsNullOrWhiteSpace(refMeta))
                    {
                        var target = refMeta!.Contains('.') ? refMeta.Split('.').Last() : refMeta;
                        if (byTable.TryGetValue(target, out var ce))
                            columns[(table.DbName.ToLowerInvariant(), col.ColumnName.ToLowerInvariant())] = ce;
                        continue;
                    }
                    // 2) FK targeting the enum table's resolved value column
                    if (fkTargets.TryGetValue((table.DbName.ToLowerInvariant(), col.ColumnName.ToLowerInvariant()), out var tgt)
                        && byTable.TryGetValue(tgt.Table, out var ce2)
                        && resolvedValueColumns.TryGetValue(tgt.Table, out var valueCol)
                        && ci.Equals(tgt.Column, valueCol))
                    {
                        columns[(table.DbName.ToLowerInvariant(), col.ColumnName.ToLowerInvariant())] = ce2;
                    }
                }
            }
            return new EnumColumnMap(columns);
        }

        private static IEnumerable<DbForeignKey> EnumColumnMapForeignKeys(IDbModel model)
            => (model as DbModel)?.ForeignKeysForEnums() ?? Enumerable.Empty<DbForeignKey>();

        public bool TryGetEnumType(string tableDbName, string columnDbName, out string enumName)
        {
            if (_columns.TryGetValue((tableDbName.ToLowerInvariant(), columnDbName.ToLowerInvariant()), out var ce))
            { enumName = ce.EnumName; return true; }
            enumName = string.Empty; return false;
        }

        public string? ValueToName(string tableDbName, string columnDbName, object? dbValue)
        {
            if (dbValue == null) return null;
            if (!_columns.TryGetValue((tableDbName.ToLowerInvariant(), columnDbName.ToLowerInvariant()), out var ce)) return null;
            return ce.ValueToName.TryGetValue(dbValue.ToString()!, out var name) ? name : null;
        }

        public string? NameToValue(string tableDbName, string columnDbName, string name)
        {
            if (!_columns.TryGetValue((tableDbName.ToLowerInvariant(), columnDbName.ToLowerInvariant()), out var ce)) return null;
            return ce.NameToValue.TryGetValue(name, out var v) ? v : null;
        }

        public bool HasAnyFor(string tableDbName)
            => _columns.Keys.Any(k => string.Equals(k.Table, tableDbName, StringComparison.OrdinalIgnoreCase));

        /// <summary>Rewrites enum-named operands to their DB values across the filter tree, in place.</summary>
        public void RewriteFilterValues(TableFilter? node, string tableDbName)
        {
            while (node != null)
            {
                if (!string.IsNullOrEmpty(node.ColumnName)
                    && _columns.TryGetValue((tableDbName.ToLowerInvariant(), node.ColumnName.ToLowerInvariant()), out var ce))
                {
                    node.Value = node.Value switch
                    {
                        string s => ce.NameToValue.TryGetValue(s, out var v) ? v : s,
                        IEnumerable<object?> list => list.Select(x =>
                            x is string xs && ce.NameToValue.TryGetValue(xs, out var lv) ? (object?)lv : x).ToList(),
                        _ => node.Value,
                    };
                }
                foreach (var child in node.And) RewriteFilterValues(child, tableDbName);
                foreach (var child in node.Or) RewriteFilterValues(child, tableDbName);
                node = node.Next;
            }
        }
    }
}
```

> NOTE: `DbModel.ForeignKeysForEnums()` is a small accessor added in this step exposing the foreign keys `DbModel` already holds (it consumes `DbForeignKey` in `LinkTables`). If `DbModel` already exposes its FK collection publicly, use that instead and drop `ForeignKeysForEnums`. Inspect `DbModel.cs` and adapt; do not invent a field that isn't backed by real data.

- [ ] **Step 4: Run tests, verify they pass**

Run: same filter as Step 2. Expected: PASS (all 5 cases).

- [ ] **Step 5: Commit**

```bash
git add src/BifrostQL.Core/Schema/EnumColumnMap.cs tests/BifrostQL.Core.Test/Unit/Schema/
git commit -m "feat(enum): EnumColumnMap resolution + value<->name + filter rewrite"
```

---

## Task 3: `EnumValueLoader` — load + sanitize distinct values (security-aware)

**Files:**
- Create: `src/BifrostQL.Core/Schema/EnumValueLoader.cs`
- Test: `tests/BifrostQL.Core.Test/Unit/Schema/EnumValueLoaderTests.cs`

`EnumValueLoader` resolves each enum table's effective value column (explicit `EnumTableConfig.ValueColumn`, else first non-PK string column), builds a `SELECT DISTINCT {valueColumn} FROM {schema}.{table}` with the dialect, **appends the WHERE clause produced by the active filter transformers** for that table (so tenant/soft-delete apply), executes it, and sanitizes the values. It returns both the `tableDbName → entries` map and the `tableDbName → resolvedValueColumn` map.

- [ ] **Step 1: Write failing test** (pure helpers tested without DB; the SELECT execution is covered by the integration task)

```csharp
using System.Linq;
using BifrostQL.Core.Schema;
using FluentAssertions;
using Xunit;

public class EnumValueLoaderColumnResolutionTests
{
    [Fact]
    public void ResolvesExplicitValueColumn()
    {
        var table = TestModel.Table("status", "status",
            cols: new[] { TestModel.Col("status", "id", isPk: true), TestModel.Col("status", "code") },
            metadata: new() { ["enum"] = "code" });
        EnumValueLoader.ResolveValueColumn(table).Should().Be("code");
    }

    [Fact]
    public void AutoResolvesFirstNonPkStringColumn()
    {
        var table = TestModel.Table("status", "status",
            cols: new[] { TestModel.Col("status", "id", isPk: true, dataType: "int"),
                          TestModel.Col("status", "label", dataType: "varchar") },
            metadata: new() { ["enum"] = "true" });
        EnumValueLoader.ResolveValueColumn(table).Should().Be("label");
    }
}
```

- [ ] **Step 2: Run, verify fail**

Run: `dotnet test tests/BifrostQL.Core.Test --framework net10.0 --filter "FullyQualifiedName~EnumValueLoaderColumnResolutionTests"`
Expected: FAIL — `EnumValueLoader` missing.

- [ ] **Step 3: Implement loader**

```csharp
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading.Tasks;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;

namespace BifrostQL.Core.Schema
{
    /// <summary>
    /// Loads and sanitizes distinct values for every table marked `enum:`, at
    /// schema-build time, through the dialect. The optional whereByTable map lets
    /// callers inject the same WHERE clause the read pipeline would (tenant /
    /// soft-delete), keeping enum membership security-scoped.
    /// </summary>
    public static class EnumValueLoader
    {
        public sealed record LoadResult(
            IReadOnlyDictionary<string, IReadOnlyList<EnumValueEntry>> Values,
            IReadOnlyDictionary<string, string> ValueColumns);

        public static string? ResolveValueColumn(IDbTable table)
        {
            var cfg = EnumTableConfig.FromTable(table);
            if (cfg == null) return null;
            if (!string.IsNullOrEmpty(cfg.ValueColumn)) return cfg.ValueColumn;
            // Auto: first non-PK column whose effective type maps to a string.
            var col = table.Columns.FirstOrDefault(c => !c.IsPrimaryKey
                && Utils.StringNormalizer.NormalizeType(c.EffectiveDataType) is var t
                && (t.Contains("char") || t.Contains("text")));
            return col?.ColumnName;
        }

        public static async Task<LoadResult> LoadAsync(
            IDbModel model, IDbConnFactory connFactory,
            IReadOnlyDictionary<string, string>? whereByTable = null)
        {
            var values = new Dictionary<string, IReadOnlyList<EnumValueEntry>>(StringComparer.OrdinalIgnoreCase);
            var valueColumns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var dialect = connFactory.Dialect;

            await using var conn = connFactory.GetConnection();
            await conn.OpenAsync();

            foreach (var table in model.Tables)
            {
                var cfg = EnumTableConfig.FromTable(table);
                if (cfg == null) continue;
                var valueColumn = ResolveValueColumn(table);
                if (valueColumn == null) continue;
                valueColumns[table.DbName] = valueColumn;

                try
                {
                    var tableRef = dialect.TableReference(table.TableSchema, table.DbName);
                    var col = dialect.EscapeIdentifier(valueColumn);
                    var where = whereByTable != null && whereByTable.TryGetValue(table.DbName, out var w) && !string.IsNullOrEmpty(w)
                        ? $" WHERE {w}" : string.Empty;
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = $"SELECT DISTINCT {col} FROM {tableRef}{where}";
                    var raw = new List<string?>();
                    await using var reader = await cmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                        raw.Add(reader.IsDBNull(0) ? null : reader.GetValue(0)?.ToString());
                    values[table.DbName] = EnumValueSanitizer.SanitizeAll(raw);
                }
                catch (DbException)
                {
                    // Degrade this enum table to scalar; the rest of the schema still builds.
                    values[table.DbName] = Array.Empty<EnumValueEntry>();
                }
            }
            return new LoadResult(values, valueColumns);
        }
    }
}
```

> NOTE: confirm the dialect method names against `src/BifrostQL.Core/QueryModel/ISqlDialect.cs` — `TableReference(schema, name)` and `EscapeIdentifier(name)` are used elsewhere (e.g. `TreeSyncStateLoader.cs`). If the signatures differ, match them.

- [ ] **Step 4: Run, verify pass**

Run: same filter as Step 2. Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/BifrostQL.Core/Schema/EnumValueLoader.cs tests/BifrostQL.Core.Test/Unit/Schema/EnumValueLoaderColumnResolutionTests.cs
git commit -m "feat(enum): EnumValueLoader loads + sanitizes distinct lookup values"
```

---

## Task 4: Carry `EnumColumnMap` on the model

**Files:**
- Modify: `src/BifrostQL.Core/Model/DbModel.cs`
- Test: `tests/BifrostQL.Core.Test/Unit/Model/DbModelEnumCarrierTests.cs`

- [ ] **Step 1: Failing test**

```csharp
using BifrostQL.Core.Model;
using FluentAssertions;
using Xunit;

public class DbModelEnumCarrierTests
{
    [Fact]
    public void EnumColumns_DefaultsNull_AndIsSettable()
    {
        var model = TestModel.Model(System.Array.Empty<DbTable>(), System.Array.Empty<DbForeignKey>());
        model.EnumColumns.Should().BeNull();
        // settable
        ((DbModel)model).EnumColumns = null;
    }
}
```

- [ ] **Step 2: Run, verify fail** — `EnumColumns` missing.

Run: `dotnet test tests/BifrostQL.Core.Test --framework net10.0 --filter "FullyQualifiedName~DbModelEnumCarrierTests"`

- [ ] **Step 3: Add the property**

In `IDbModel`:
```csharp
/// <summary>Enum-column resolution/translation for lookup-table enums; null when none configured.</summary>
BifrostQL.Core.Schema.EnumColumnMap? EnumColumns => null;
```
In `DbModel` (next to `EavConfigs`):
```csharp
public BifrostQL.Core.Schema.EnumColumnMap? EnumColumns { get; set; }
```

- [ ] **Step 4: Run, verify pass.**

- [ ] **Step 5: Commit**

```bash
git add src/BifrostQL.Core/Model/DbModel.cs tests/BifrostQL.Core.Test/Unit/Model/DbModelEnumCarrierTests.cs
git commit -m "feat(enum): carry EnumColumnMap on DbModel"
```

---

## Task 5: Load + attach during schema build (`DbModelLoader` + `ProfileModelCache`)

**Files:**
- Modify: `src/BifrostQL.Core/Model/DbModelLoader.cs`
- Modify: `src/BifrostQL.Core/Schema/ProfileModelCache.cs`
- Test: `tests/BifrostQL.Integration.Test/SchemaLoading/EnumLoadingTests.cs` (Sqlite — fast, always available)

- [ ] **Step 1: Failing integration test (Sqlite)**

```csharp
// Seeds a lookup table `status(code)` with values, marks it enum, and asserts the
// built model exposes an EnumColumnMap that maps a referencing column.
using System.Threading.Tasks;
using BifrostQL.Core.Model;
using FluentAssertions;
using Xunit;

public class EnumLoadingTests
{
    [Fact]
    public async Task BuiltModel_HasEnumColumnMap_ForLookupTable()
    {
        // Arrange: use the Sqlite integration harness to create:
        //   CREATE TABLE status (id INTEGER PRIMARY KEY, code TEXT);
        //   INSERT INTO status(code) VALUES ('active'),('inactive');
        //   CREATE TABLE orders (id INTEGER PRIMARY KEY, status TEXT REFERENCES status(code));
        // with metadata "status { enum: code }".
        var (connFactory, metadataRules) = await EnumTestHarness.SqliteAsync();
        var loader = new DbModelLoader(connFactory, new MetadataLoader(metadataRules));
        var model = await loader.LoadAsync();

        // Act
        var enumValues = await loader.LoadEnumValuesAsync(model);

        // Assert
        enumValues.Values.Should().ContainKey("status");
        enumValues.Values["status"].Select(e => e.GraphQlName).Should().Contain(new[] { "ACTIVE", "INACTIVE" });
        var map = BifrostQL.Core.Schema.EnumColumnMap.Build(model, enumValues.Values, enumValues.ValueColumns);
        map.TryGetEnumType("orders", "status", out var name).Should().BeTrue();
        name.Should().Be("statusValues");
    }
}
```

> NOTE: `EnumTestHarness.SqliteAsync()` builds an in-memory SQLite DB + connFactory + metadata rules. Reuse the existing Sqlite integration fixtures in `tests/BifrostQL.Integration.Test/Infrastructure/SqliteTestDatabase.cs` patterns; add the harness helper there.

- [ ] **Step 2: Run, verify fail** — `LoadEnumValuesAsync` missing.

Run: `dotnet test tests/BifrostQL.Integration.Test --framework net8.0 --filter "FullyQualifiedName~EnumLoadingTests"`

- [ ] **Step 3: Add `LoadEnumValuesAsync` to `DbModelLoader`**

```csharp
public Task<BifrostQL.Core.Schema.EnumValueLoader.LoadResult> LoadEnumValuesAsync(IDbModel model)
    => BifrostQL.Core.Schema.EnumValueLoader.LoadAsync(model, _connFactory);
```

- [ ] **Step 4: Attach in `ProfileModelCache`**

In `ProfileModelCache`, after a model is built for a profile and before it is returned/cached, build and attach the map once (cache the `LoadResult` on the instance, like the shared read). Concretely: add a field `private BifrostQL.Core.Schema.EnumValueLoader.LoadResult? _enumValues;` and a guarded async load that runs once; in the model-build path set `model.EnumColumns = EnumColumnMap.Build(model, _enumValues.Values, _enumValues.ValueColumns);`. Clear `_enumValues` in `Reset`.

> NOTE: `ProfileModelCache.GetFor` is currently synchronous. Loading enum values requires async. Read `ProfileModelCache.cs` and `Extensions.cs` (the `extensionsLoader.AddLoader(... async () => { ... profileCache.GetFor(null) ... })` site) and thread an async build: either make the enum load part of the async loader body (load once there, pass the `LoadResult` into the cache constructor) — preferred, since the loader is already async and already holds `read`. Implement whichever keeps `GetFor` synchronous by passing pre-loaded enum values into `ProfileModelCache`'s constructor.

- [ ] **Step 5: Run, verify pass.**

- [ ] **Step 6: Commit**

```bash
git add src/BifrostQL.Core/Model/DbModelLoader.cs src/BifrostQL.Core/Schema/ProfileModelCache.cs tests/BifrostQL.Integration.Test/
git commit -m "feat(enum): load enum values at schema build and attach EnumColumnMap to model"
```

---

## Task 6: Emit enum + filter-input types (`SchemaGenerator`)

**Files:**
- Modify: `src/BifrostQL.Core/Schema/SchemaGenerator.cs`
- Test: `tests/BifrostQL.Core.Test/Unit/Schema/EnumSchemaEmissionTests.cs`

- [ ] **Step 1: Failing test**

```csharp
using BifrostQL.Core.Schema;
using FluentAssertions;
using Xunit;

public class EnumSchemaEmissionTests
{
    [Fact]
    public void Schema_Includes_EnumType_And_FilterInput()
    {
        var (model, values, valueCols) = EnumColumnMapTests_Fixture(); // shared fixture from Task 2
        model.EnumColumns = EnumColumnMap.Build(model, values, valueCols);
        var text = SchemaGenerator.SchemaTextFromModel(model);
        text.Should().Contain("enum statusValues {");
        text.Should().Contain("ACTIVE");
        text.Should().Contain("input FilterTypestatusValuesInput {");
    }
}
```

- [ ] **Step 2: Run, verify fail.**

- [ ] **Step 3: Emit types in `SchemaTextFromModel`**

After the existing type emission loop, append for each enum table (using the model's `EnumColumns` and the per-table entries — store the entries on `EnumColumnMap` so the generator can read them, e.g. add `IReadOnlyDictionary<string,(string EnumName, IReadOnlyList<EnumValueEntry> Entries)> EnumTables { get; }` to `EnumColumnMap`):

```csharp
if (model.EnumColumns != null)
{
    foreach (var (tableDbName, info) in model.EnumColumns.EnumTables)
    {
        var table = model.Tables.First(t => string.Equals(t.DbName, tableDbName, System.StringComparison.OrdinalIgnoreCase));
        var cfg = EnumTableConfig.FromTable(table)!;
        var gen = new EnumTableSchemaGenerator(cfg, info.Entries);
        builder.AppendLine(gen.GenerateEnumTypeDefinition());
        builder.AppendLine(gen.GetFilterTypeDefinition());
    }
}
```

Add the `EnumTables` accessor to `EnumColumnMap` (it already holds the per-table value/name dicts; expose name + entries). Update Task 2's class accordingly and re-run its tests.

- [ ] **Step 4: Run, verify pass.**

- [ ] **Step 5: Commit**

```bash
git add src/BifrostQL.Core/Schema/SchemaGenerator.cs src/BifrostQL.Core/Schema/EnumColumnMap.cs tests/BifrostQL.Core.Test/Unit/Schema/EnumSchemaEmissionTests.cs
git commit -m "feat(enum): emit enum + filter-input types from EnumColumnMap"
```

---

## Task 7: Render enum columns as the enum type (`TableSchemaGenerator`)

**Files:**
- Modify: `src/BifrostQL.Core/Schema/TableSchemaGenerator.cs`
- Test: `tests/BifrostQL.Core.Test/Unit/Schema/EnumColumnRenderingTests.cs`

`TableSchemaGenerator` currently renders `column.GraphQlName : GetGraphQlTypeName(EffectiveDataType,...)` (field) and `column.GraphQlName : GetFilterInputTypeName(EffectiveDataType,...)` (filter). The generator must know the `EnumColumnMap`. Thread it via the constructor.

- [ ] **Step 1: Failing test** — build a `TableSchemaGenerator` for `orders` with an enum map and assert the `status` field renders as `statusValues` and its filter arg as `FilterTypestatusValuesInput`.

- [ ] **Step 2: Run, verify fail.**

- [ ] **Step 3: Implement**

Add an optional `EnumColumnMap? enumColumns` parameter to the `TableSchemaGenerator` constructors (default null; `SchemaGenerator` passes `model.EnumColumns`). In the field-render loop (`GetTableTypeDefinition` ~line 75) and the filter-render loop (`GetTableFilterDefinition` ~line 318), prefer the enum type when present:

```csharp
// field type
string fieldType = _enumColumns != null && _enumColumns.TryGetEnumType(_table.DbName, column.ColumnName, out var en)
    ? (column.IsNullable ? en : en + "!")
    : SchemaGenerator.GetGraphQlTypeName(column.EffectiveDataType, column.IsNullable, _typeMapper);
builder.AppendLine($"\t{column.GraphQlName} : {fieldType}");

// filter type
string filterType = _enumColumns != null && _enumColumns.TryGetEnumType(_table.DbName, column.ColumnName, out var fe)
    ? $"FilterType{fe}Input"
    : SchemaGenerator.GetFilterInputTypeName(column.EffectiveDataType, _typeMapper);
builder.AppendLine($"\t{column.GraphQlName} : {filterType}");
```

Update `SchemaGenerator.SchemaTextFromModel` to construct `new TableSchemaGenerator(t, typeMapper, model.EnumColumns)`.

- [ ] **Step 4: Run, verify pass.** Also run the full schema snapshot suite to refresh any golden files: `dotnet test tests/BifrostQL.Core.Test --framework net10.0 --filter "FullyQualifiedName~Schema"` and update snapshots intentionally where the new enum types/columns appear.

- [ ] **Step 5: Commit**

```bash
git add src/BifrostQL.Core/Schema/TableSchemaGenerator.cs src/BifrostQL.Core/Schema/SchemaGenerator.cs tests/BifrostQL.Core.Test/
git commit -m "feat(enum): render enum columns as enum type + enum filter input"
```

---

## Task 8: Read projection value→name (`ReaderEnum`)

**Files:**
- Modify: `src/BifrostQL.Core/Resolvers/ReaderEnum.cs`
- Modify: `src/BifrostQL.Core/Resolvers/SqlExecutionManager.cs` (pass `EnumColumnMap` + a logger into `ReaderEnum`)
- Test: `tests/BifrostQL.Core.Test/Unit/Resolvers/ReaderEnumEnumMappingTests.cs`

- [ ] **Step 1: Failing test** — construct a `ReaderEnum` whose table is `orders` with one row `status = "active"`, an `EnumColumnMap` mapping `orders.status`, and assert `Get(0, ctx for "status")` yields `"ACTIVE"`; a row with `status = "gone"` yields `null`.

- [ ] **Step 2: Run, verify fail.**

- [ ] **Step 3: Implement**

Give `ReaderEnum` the `EnumColumnMap?` and the table DbName (available from `_tableSql.DbTable.DbName`). In `Get`, after resolving the raw value, if the column (by `context.FieldName`/alias mapped to the column DbName) is an enum, translate:

```csharp
var raw = DbConvert(table.data[row][index]);
if (_enumColumns != null)
{
    var name = _enumColumns.ValueToName(_tableSql.DbTable.DbName, /*column db name for this field*/ columnDbName, raw);
    if (name != null) return ValueTask.FromResult<object?>(name);
    if (raw != null && _enumColumns.TryGetEnumType(_tableSql.DbTable.DbName, columnDbName, out _))
    {
        _logger?.LogWarning("Enum drift: value '{Value}' on {Table}.{Column} is not a declared enum member; returning null.",
            raw, _tableSql.DbTable.DbName, columnDbName);
        return ValueTask.FromResult<object?>(null);
    }
}
return ValueTask.FromResult(raw);
```

Resolve `columnDbName` from the field: the GraphQL field name maps to a column via `_tableSql.DbTable` column lookup by GraphQlName. Use the existing table column metadata to translate `context.FieldName`→column DbName (reuse whatever mapping `GqlObjectQuery`/`DbTable` already exposes; inspect `ReaderEnum` neighbors). `SqlExecutionManager` constructs `ReaderEnum` (two call sites — `TableResult.Data` and the bare return); pass `_dbModel.EnumColumns` and an `ILogger?` to both.

- [ ] **Step 4: Run, verify pass.**

- [ ] **Step 5: Commit**

```bash
git add src/BifrostQL.Core/Resolvers/ReaderEnum.cs src/BifrostQL.Core/Resolvers/SqlExecutionManager.cs tests/BifrostQL.Core.Test/Unit/Resolvers/
git commit -m "feat(enum): map stored value to enum name on read (null+warn on drift)"
```

---

## Task 9: Filter value rewrite (`SqlExecutionManager`)

**Files:**
- Modify: `src/BifrostQL.Core/Resolvers/SqlExecutionManager.cs`
- Test: `tests/BifrostQL.Core.Test/Unit/Resolvers/EnumFilterRewriteTests.cs`

- [ ] **Step 1: Failing test** — build a `GqlObjectQuery` for `orders` whose `Filter` is `status _eq "ACTIVE"`, with a model whose `EnumColumns` maps `orders.status`; run the rewrite step; assert the filter `Value` is `"active"` before SQL generation. (Drive via a small seam: extract the rewrite into a method `ApplyEnumFilterRewrite(GqlObjectQuery table)` on `SqlExecutionManager` and test it directly, or assert on the generated SQL parameters.)

- [ ] **Step 2: Run, verify fail.**

- [ ] **Step 3: Implement** — in `ResolveAsync`, after `_transformerService.ApplyTransformers(table, _dbModel, userContext)` and before `LoadDataParameterizedAsync`:

```csharp
_dbModel.EnumColumns?.RewriteFilterValues(table.Filter, table.DbTable.DbName);
```

If nested joins carry their own filters, walk those too (each `TableJoin.ConnectedTable.Filter` with that table's DbName). Inspect `GqlObjectQuery` for the join/filter structure and rewrite each connected table's filter with its own DbName.

- [ ] **Step 4: Run, verify pass.**

- [ ] **Step 5: Commit**

```bash
git add src/BifrostQL.Core/Resolvers/SqlExecutionManager.cs tests/BifrostQL.Core.Test/Unit/Resolvers/EnumFilterRewriteTests.cs
git commit -m "feat(enum): rewrite enum filter operands name->value before SQL"
```

---

## Task 10: Mutation value rewrite (`EnumValueMutationTransformer`)

**Files:**
- Create: `src/BifrostQL.Core/Modules/EnumValueMutationTransformer.cs`
- Register: wherever built-in mutation transformers are registered (`BifrostServiceCollectionExtensions` — search `WithBuiltInMutationTransformers`).
- Test: `tests/BifrostQL.Core.Test/Unit/Modules/EnumValueMutationTransformerTests.cs`

- [ ] **Step 1: Failing test** — a transformer given `data = { ["status"] = "ACTIVE" }` for table `orders` with a model `EnumColumns` mapping `orders.status` returns `data` with `["status"] = "active"`; an unknown name yields an error result.

- [ ] **Step 2: Run, verify fail.**

- [ ] **Step 3: Implement** (raw `IMutationTransformer`, not the single-column base, since it spans all enum columns)

```csharp
using System.Collections.Generic;
using System.Linq;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;

namespace BifrostQL.Core.Modules
{
    /// <summary>Rewrites enum-named input values to their stored DB values for enum columns, on insert/update/upsert.</summary>
    public sealed class EnumValueMutationTransformer : IMutationTransformer, IModuleNamed
    {
        public string ModuleName => "enum-value";
        public int Priority => 150; // data-filtering band, before SQL build

        public bool AppliesTo(IDbTable table, MutationType mutationType, MutationTransformContext context)
            => context.Model.EnumColumns?.HasAnyFor(table.DbName) == true;

        public MutationTransformResult Transform(IDbTable table, MutationType mutationType,
            Dictionary<string, object?> data, MutationTransformContext context)
        {
            var map = context.Model.EnumColumns!;
            var errors = new List<string>();
            var output = new Dictionary<string, object?>(data);
            foreach (var kv in data)
            {
                var column = table.Columns.FirstOrDefault(c => string.Equals(c.GraphQlName, kv.Key, System.StringComparison.OrdinalIgnoreCase));
                if (column == null) continue;
                if (!map.TryGetEnumType(table.DbName, column.ColumnName, out _)) continue;
                if (kv.Value is string name)
                {
                    var dbValue = map.NameToValue(table.DbName, column.ColumnName, name);
                    if (dbValue == null) { errors.Add($"'{name}' is not a valid {column.GraphQlName} value."); continue; }
                    output[kv.Key] = dbValue;
                }
            }
            return new MutationTransformResult { MutationType = mutationType, Data = output,
                Errors = errors.Count > 0 ? errors.ToArray() : null };
        }
    }
}
```

> NOTE: confirm `MutationTransformResult`'s exact shape (`MutationType`, `Data`, `Errors`) and `MutationType` enum values against `IMutationTransformer.cs`. Match the real members. Register the transformer in the built-in list with priority 150.

- [ ] **Step 4: Run, verify pass.**

- [ ] **Step 5: Commit**

```bash
git add src/BifrostQL.Core/Modules/EnumValueMutationTransformer.cs src/BifrostQL.Server/BifrostServiceCollectionExtensions.cs tests/BifrostQL.Core.Test/Unit/Modules/
git commit -m "feat(enum): rewrite enum input values name->value on mutation"
```

---

## Task 11: Security — enum loading honors the read filter pipeline

**Files:**
- Modify: `src/BifrostQL.Core/Schema/EnumValueLoader.cs` (accept `whereByTable`)
- Modify: the schema-build site (`Extensions.cs` async loader / `ProfileModelCache`) to compute the WHERE per enum table from the active filter transformers and pass it in.
- Test: `tests/BifrostQL.Integration.Test/SchemaLoading/EnumSecurityTests.cs` (Sqlite)

- [ ] **Step 1: Failing test** — seed a lookup table with a `tenant_id` column and `tenant-filter: tenant_id` metadata; with a user context selecting tenant A, assert the loaded enum values include only tenant A's rows.

- [ ] **Step 2: Run, verify fail.**

- [ ] **Step 3: Implement** — at the build site, for each enum table, run the filter transformers (the same ones the read path uses) to produce that table's WHERE SQL + parameters; pass a `tableDbName → whereSql` map into `EnumValueLoader.LoadAsync`. Because parameters are involved, extend `LoadAsync` to accept the rendered `ParameterizedSql` per table (WHERE text + `SqlParameterInfo[]`), and bind them with `DbParameterBinder.AddExtraParameters` (reuse the existing helper). Inspect how the read path renders a table's filter (`GqlObjectQuery.GetFilterSqlParameterized`) and reuse it to produce the WHERE for the bare lookup table.

> NOTE: this couples enum loading to the transformer pipeline. Keep `LoadAsync`'s no-WHERE overload for unit tests. The build site is the only caller that passes WHERE clauses.

- [ ] **Step 4: Run, verify pass.**

- [ ] **Step 5: Commit**

```bash
git add src/BifrostQL.Core/Schema/EnumValueLoader.cs src/BifrostQL.Server/BifrostServiceCollectionExtensions.cs src/BifrostQL.Core/Schema/ProfileModelCache.cs tests/BifrostQL.Integration.Test/SchemaLoading/EnumSecurityTests.cs
git commit -m "feat(enum): scope enum value loading through the read filter pipeline"
```

---

## Task 12: End-to-end integration across all four engines

**Files:**
- Test: `tests/BifrostQL.Integration.Test/FullIntegration/EnumColumnIntegrationTests.cs`

Use the existing `DatabaseFixture<T>` / per-engine harness (`SqlServerTestDatabase`, `PostgresTestDatabase`, `MySqlTestDatabase`, `SqliteTestDatabase`). Tests skip when the engine env var is unset (existing `EnsureAvailable()` pattern).

- [ ] **Step 1: Write the integration tests** (one parameterized base, four engine subclasses, mirroring the existing `*FullIntegrationTests` structure)

For each engine, against a seeded `status(code)` lookup (`active`/`inactive`, enum:code) and an `orders(status)` table:

```csharp
[SkippableFact]
public async Task Read_MapsStoredValueToEnumName()
{
    Fixture.EnsureAvailable();
    var json = await Execute("{ orders { data { id status } } }");
    json.Should().Contain("\"status\":\"ACTIVE\"");
}

[SkippableFact]
public async Task Filter_ByEnumName_ReturnsMatchingRows()
{
    Fixture.EnsureAvailable();
    var json = await Execute("{ orders(filter:{ status:{ _eq: ACTIVE } }) { total } }");
    // assert only active orders counted
}

[SkippableFact]
public async Task Insert_ByEnumName_PersistsUnderlyingValue()
{
    Fixture.EnsureAvailable();
    await Execute("mutation { orders(insert:{ status: INACTIVE }) }");
    // assert the row stored 'inactive'
}

[SkippableFact]
public async Task Read_UnknownStoredValue_ResolvesNullWithWarning()
{
    Fixture.EnsureAvailable();
    // insert a raw 'archived' bypassing the enum, then read -> status null
    var json = await Execute("{ orders { data { id status } } }");
    json.Should().Contain("\"status\":null");
}
```

- [ ] **Step 2: Run against Sqlite first**

Run: `dotnet test tests/BifrostQL.Integration.Test --framework net8.0 --filter "FullyQualifiedName~Sqlite&FullyQualifiedName~EnumColumn"`
Expected: PASS.

- [ ] **Step 3: Run against live engines**

Bring up the docker stack (`docker compose -f docker-compose.test.yml up -d`, plus Sqlserver/pg/mysql per the test-env). Set `BIFROST_TEST_POSTGRES/MYSQL/SQLSERVER`. Run:
`dotnet test tests/BifrostQL.Integration.Test --framework net8.0 --filter "FullyQualifiedName~EnumColumn"`
Expected: PASS on all available engines, 0 skipped where env vars are set.

- [ ] **Step 4: Full regression**

Run the whole solution: `dotnet test BifrostQL.sln -c Debug`. Expected: all green (no regressions in existing schema snapshots, resolvers, or transformers).

- [ ] **Step 5: Commit**

```bash
git add tests/BifrostQL.Integration.Test/FullIntegration/EnumColumnIntegrationTests.cs
git commit -m "test(enum): end-to-end enum column read/filter/write/drift across engines"
```

---

## Task 13: Docs

**Files:**
- Modify: `docs/src/content/docs/concepts/` (add `lookup-table-enums.md`)
- Modify: `CHANGELOG.md`

- [ ] **Step 1:** Document the `enum:` / `enum-ref:` metadata, the value-valued (Approach A) model, security scoping (per-connection membership), drift behavior (null+warning), and the FK-by-id non-support. Add a CHANGELOG entry.

- [ ] **Step 2: Commit**

```bash
git add docs/ CHANGELOG.md
git commit -m "docs(enum): lookup-table enum wiring concept + changelog"
```

---

## Self-review notes (addressed)

- **Spec coverage:** loader (T3), column map (T2), carrier (T4), build/attach (T5), schema emission (T6/T7), read mapping (T8), filter mapping (T9), write mapping (T10), security (T11), 4-DB integration (T12), drift behavior (T8/T12), docs (T13). All spec sections map to a task.
- **Known seams flagged for the implementer** (verify against real signatures before coding): `DbModel` FK accessor for `EnumColumnMap`; `ISqlDialect.TableReference/EscapeIdentifier`; `ProfileModelCache.GetFor` async threading; `MutationTransformResult` member names; `ReaderEnum` field→column-DbName resolution; built-in mutation transformer registration site. Each note says exactly which file to read.
- **Type consistency:** `EnumColumnMap.TryGetEnumType/ValueToName/NameToValue/RewriteFilterValues/HasAnyFor/EnumTables`, `EnumValueLoader.LoadResult{Values,ValueColumns}`/`ResolveValueColumn`/`LoadAsync`, `DbModel.EnumColumns`, `MetadataKeys.Enum.Ref` used identically across tasks.
- **Scope:** single feature; FK-by-id and pivot explicitly excluded.
