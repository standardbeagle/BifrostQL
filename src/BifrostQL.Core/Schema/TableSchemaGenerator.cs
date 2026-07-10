using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules.ComputedColumns;

namespace BifrostQL.Core.Schema
{
    internal sealed class TableSchemaGenerator
    {
        private readonly IDbTable _table;
        private readonly ITypeMapper _typeMapper;
        private readonly EnumColumnMap? _enumColumns;

        public TableSchemaGenerator(IDbTable table) : this(table, SchemaGenerator.DefaultTypeMapper)
        {
        }

        public TableSchemaGenerator(IDbTable table, ITypeMapper typeMapper, EnumColumnMap? enumColumns = null)
        {
            _table = table;
            _typeMapper = typeMapper;
            _enumColumns = enumColumns;
        }

        /// <summary>
        /// A column is emitted to the GraphQL surface unless its visibility
        /// metadata marks it hidden — mirroring the table-level hide rule
        /// (<c>visibility: hidden</c>) applied in <see cref="Model.DbModel.FromTables"/>.
        /// Hidden columns remain in the model (joins, SQL, identity still use them);
        /// only schema emission omits them. Because per-profile models are built
        /// from each profile's metadata, this yields per-profile reduced column sets.
        /// </summary>
        private static bool IsColumnVisible(ColumnDto col) =>
            !col.CompareMetadata(MetadataKeys.Ui.Visibility, MetadataKeys.Ui.Hidden);

        private IEnumerable<ColumnDto> VisibleColumns => _table.Columns.Where(IsColumnVisible);

        /// <summary>
        /// Computed columns emitted for this table. The model is required so EAV
        /// parent tables surface their synthesized <c>_meta</c> provider column.
        /// </summary>
        private IEnumerable<ComputedColumnDefinition> ComputedColumnsFor(IDbModel model)
            => ComputedColumnConfigCollector.FromTable(_table, model);

        /// <summary>
        /// True when a single-link's FK column(s) on this table resolve to a
        /// lookup-table enum. Delegates to <see cref="EnumColumnMap.IsEnumLink"/>
        /// (single source of truth shared with <c>BifrostDispatcher</c>) so the
        /// suppression rule cannot drift between schema emission and resolver wiring.
        /// </summary>
        private bool IsEnumColumnLink(TableLinkDto link) =>
            _enumColumns != null && _enumColumns.IsEnumLink(_table.DbName, link);

        /// <summary>
        /// Which GraphQL emission surface a field type is being resolved for. Each
        /// selects a different base-type helper and enum-nullability convention.
        /// </summary>
        private enum FieldTypeKind
        {
            /// <summary>Output object type — enum honours <paramref>nullable</paramref>.</summary>
            Type,
            /// <summary>Mutation input type — enum honours <paramref>nullable</paramref>.</summary>
            Insert,
            /// <summary>Nested-sync input — every field optional; enum emitted bare.</summary>
            Sync,
            /// <summary>Filter input — enum maps to its FilterType…Input wrapper.</summary>
            Filter,
        }

        /// <summary>
        /// Resolves the GraphQL type name for a column on the given emission surface,
        /// folding the enum-column lookup and the per-surface base-type helper into one
        /// place. Enum columns resolve to their enum type name (with the surface's
        /// nullability convention); non-enum columns fall back to the matching
        /// <see cref="SchemaGenerator"/> helper. Each call site passes the exact
        /// nullability it previously computed, so the emitted type is unchanged.
        /// </summary>
        private string ResolveFieldType(ColumnDto column, bool nullable, FieldTypeKind kind)
        {
            if (_enumColumns != null && _enumColumns.TryGetEnumType(_table.DbName, column.ColumnName, out var enumName))
            {
                return kind switch
                {
                    FieldTypeKind.Filter => $"FilterType{enumName}Input",
                    FieldTypeKind.Sync => enumName,
                    _ => nullable ? enumName : enumName + "!",
                };
            }

            return kind switch
            {
                FieldTypeKind.Type => SchemaGenerator.GetGraphQlTypeName(column.EffectiveDataType, nullable, _typeMapper),
                FieldTypeKind.Insert or FieldTypeKind.Sync => SchemaGenerator.GetGraphQlInsertTypeName(column.EffectiveDataType, nullable, _typeMapper),
                FieldTypeKind.Filter => SchemaGenerator.GetFilterInputTypeName(column.EffectiveDataType, _typeMapper),
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
            };
        }

        /// <summary>
        /// True when the column carries an AutoPopulate marker whose value is one of
        /// the recognized audit populators (created/updated/deleted on/by). Such
        /// columns are stamped server-side, so their mutation-input field is emitted
        /// nullable regardless of the underlying column's NOT NULL constraint.
        /// </summary>
        private static bool IsAutoPopulated(ColumnDto column)
            => column.GetMetadataValue(MetadataKeys.AutoPopulate.Marker) is { } value
               && MetadataKeys.AutoPopulate.KnownPopulators.Contains(value);

        public string GetTableFieldDefinition()
        {
            var moduleArgs = Modules.ModuleApiRegistry.QueryArgumentsSdl(_table);
            return
                $"{_table.GraphQlName}(limit: Int, offset: Int, sort: [{_table.TableColumnSortEnumName}!] filter: {_table.TableFilterTypeName} _primaryKey: [String]{moduleArgs}): {_table.GraphQlName}_paged";
        }

        public string GetDynamicJoinDefinition(IDbModel model, bool single)
        {
            var builder = new StringBuilder();
            builder.AppendLine($"type {_table.GraphQlName}_{(single ? "single" : "join")} {{");
            foreach (var joinTable in model.Tables)
            {
                //if (single)
                //{
                builder.AppendLine(
                    $"\t{joinTable.GraphQlName}(on: [String!]) : {joinTable.GraphQlName}");
                //}
                //else
                //{
                //    builder.AppendLine(
                //        $"\t{joinTable.GraphQlName}(on: TableOn{_table.GraphQlName}{joinTable.GraphQlName}, filter: {joinTable.TableFilterTypeName}, sort: [{joinTable.TableColumnSortEnumName}!]) : [{joinTable.GraphQlName}!]!");
                //}
            }
            builder.AppendLine("}");
            return builder.ToString();
        }

        public string GetTableTypeDefinition(IDbModel model, bool includeDynamicJoins)
        {
            var builder = new StringBuilder();
            builder.AppendLine($"type {_table.GraphQlName} {{");
            foreach (var column in VisibleColumns)
            {
                var fieldType = ResolveFieldType(column, column.IsNullable, FieldTypeKind.Type);
                builder.AppendLine($"\t{column.GraphQlName} : {fieldType}");
            }

            foreach (var column in ComputedColumnsFor(model))
            {
                builder.AppendLine($"\t{column.Name} : {column.GraphQlType}");
            }

            builder.AppendLine($"_agg(operation: AggregateOperations! value: {_table.AggregateValueTypeName}!) : Float");
            // Track emitted relationship field names so self-referential tables
            // (single-link + multi-link both keyed by the same GraphQlName) don't
            // double-register the same field and crash schema build.
            var emittedLinkFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var link in _table.SingleLinks)
            {
                if (IsEnumColumnLink(link.Value)) continue;
                var fieldName = link.Value.ParentFieldName;
                if (!emittedLinkFields.Add(fieldName)) continue;
                builder.AppendLine($"\t{fieldName} : {link.Value.ParentTable.GraphQlName}");
            }
            foreach (var link in _table.MultiLinks)
            {
                var fieldName = link.Value.ChildFieldName;
                if (!emittedLinkFields.Add(fieldName)) continue;
                var child = link.Value.ChildTable;
                var childModuleArgs = Modules.ModuleApiRegistry.QueryArgumentsSdl(child);
                builder.AppendLine($"\t{fieldName}(filter: {child.TableFilterTypeName}, limit: Int, offset: Int, sort: [{child.TableColumnSortEnumName}!]{childModuleArgs}) : {child.GraphQlName}_paged");
            }
            foreach (var link in _table.ManyToManyLinks)
            {
                if (!emittedLinkFields.Add(link.Value.TargetTable.GraphQlName))
                    continue;
                var target = link.Value.TargetTable;
                var targetModuleArgs = Modules.ModuleApiRegistry.QueryArgumentsSdl(target);
                builder.AppendLine($"\t{target.GraphQlName}(filter: {target.TableFilterTypeName}, limit: Int, offset: Int, sort: [{target.TableColumnSortEnumName}!]{targetModuleArgs}) : {target.GraphQlName}_paged");
            }

            // The EAV-parent _meta field is now emitted through the ComputedColumns
            // loop above (synthesized by ComputedColumnConfigCollector.AddEavMeta),
            // so it resolves via the provider-computed-column pipeline rather than
            // being a dead schema-only stub.

            if (includeDynamicJoins)
            {
                builder.AppendLine($"\t_single : {_table.GraphQlName}_single");
                builder.AppendLine($"\t_join : {_table.GraphQlName}_join");
                //foreach (var joinTable in model.Tables)
                //{
                //    builder.AppendLine(
                //        $"\t_join_{joinTable.GraphQlName}(on: TableOn{_table.GraphQlName}{joinTable.GraphQlName}, filter: {joinTable.TableFilterTypeName}, sort: [{joinTable.TableColumnSortEnumName}!]) : [{joinTable.GraphQlName}!]!");
                //    builder.AppendLine(
                //        $"\t_single_{joinTable.GraphQlName}(on: [String!]) : {joinTable.GraphQlName}");
                //}
            }

            builder.AppendLine("}");

            return builder.ToString();
        }

        public string GetPagedTableTypeDefinition()
        {
            var builder = new StringBuilder();
            builder.AppendLine($"type {_table.GraphQlName}_paged {{");
            builder.AppendLine($"\tdata:[{_table.GraphQlName}]");
            builder.AppendLine("\ttotal: Int!");
            builder.AppendLine("\toffset: Int");
            builder.AppendLine("\tlimit: Int");
            builder.AppendLine("}");
            return builder.ToString();
        }

        public string GetTableColumnEnumDefinition()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"enum {_table.ColumnEnumTypeName} {{");
            foreach (var column in VisibleColumns)
            {
                sb.AppendLine($"    {column.GraphQlName},");
            }
            sb.AppendLine("}");
            return sb.ToString();
        }

        public string GetTableSortEnumDefinition()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"enum {_table.TableColumnSortEnumName} {{");
            foreach (var column in VisibleColumns)
            {
                sb.AppendLine($"    {column.GraphQlName}_asc,");
                sb.AppendLine($"    {column.GraphQlName}_desc,");
            }
            sb.AppendLine("}");
            return sb.ToString();
        }

        public string GetFieldEnumReference()
        {
            return $"{_table.ColumnEnumTypeName}";
        }

        public string GetMutationParameterType(MutateActions action, IdentityType identityType, bool isDelete = false)
        {
            var result = new StringBuilder();
            var name = _table.GetActionTypeName(action);
            result.AppendLine($"input {name} {{");
            foreach (var column in _table.Columns)
            {
                if (!IsColumnVisible(column))
                    continue;
                if (identityType == IdentityType.None && column.IsIdentity)
                    continue;
                if (column.IsComputed)
                    continue;

                var isNullable = column.IsNullable;
                if (IsAutoPopulated(column))
                    isNullable = true;
                if (column.IsIdentity)
                    isNullable = identityType switch
                    {
                        IdentityType.Optional => true,
                        IdentityType.Required => false,
                        IdentityType.None => true,
                        _ => throw new ArgumentOutOfRangeException(nameof(identityType), identityType, null)
                    };

                //All columns except primary keys are nullable for delete
                if (isDelete) isNullable = column.IsPrimaryKey == false;

                var insertType = ResolveFieldType(column, isNullable, FieldTypeKind.Insert);
                result.AppendLine($"\t{column.GraphQlName} : {insertType}");
            }
            result.AppendLine("}");
            return result.ToString();
        }

        /// <summary>
        /// The GraphQL input type name for a nested ("tree") insert rooted at this table.
        /// </summary>
        public string NestedSyncInsertTypeName => $"{_table.GraphQlName}_sync_insert";

        /// <summary>
        /// Emits the nested-insert input type: insertable scalar columns (all
        /// optional — foreign keys and polymorphic discriminators are auto-filled
        /// from the parent at execution) plus one child-collection field per
        /// multi-link referencing the child's own nested-insert type. Input types
        /// may reference each other cyclically, so no depth bound is needed in the
        /// schema; <see cref="Modules.TreeSyncOptions.MaxDepth"/> bounds runtime.
        /// </summary>
        public string GetNestedSyncInputType()
        {
            var result = new StringBuilder();
            result.AppendLine($"input {NestedSyncInsertTypeName} {{");
            foreach (var column in _table.Columns)
            {
                if (!IsColumnVisible(column))
                    continue;
                if (column.IsComputed)
                    continue;
                // Primary keys are included (optional): a row with a key is
                // reconciled against the existing row (update / orphan-detect);
                // a row without one is inserted.
                var syncType = ResolveFieldType(column, true, FieldTypeKind.Sync);
                result.AppendLine($"\t{column.GraphQlName} : {syncType}");
            }
            // Child collections — dedupe self-FK fields that share a GraphQlName.
            var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var link in _table.MultiLinks)
            {
                var fieldName = link.Value.ChildFieldName;
                if (!emitted.Add(fieldName)) continue;
                result.AppendLine($"\t{fieldName} : [{link.Value.ChildTable.GraphQlName}_sync_insert!]");
            }
            result.AppendLine("}");
            return result.ToString();
        }

        public string GetBatchMutationParameterType()
        {
            var result = new StringBuilder();
            var name = "batch_" + _table.GraphQlName;
            result.AppendLine($"input {name} {{");
            result.AppendLine($"insert: {_table.GetActionTypeName(MutateActions.Insert)}");
            result.AppendLine($"update: {_table.GetActionTypeName(MutateActions.Update)}");
            result.AppendLine($"upsert: {_table.GetActionTypeName(MutateActions.Upsert)}");
            result.AppendLine($"delete: {_table.GetActionTypeName(MutateActions.Delete)}");
            result.AppendLine("}");
            return result.ToString();
        }

        public string GetJoinDefinitions(IDbModel model)
        {
            var builder = new StringBuilder();
            foreach (var joinTable in model.Tables)
            {
                builder.AppendLine($"input {_table.GetJoinTypeName(joinTable)} {{");
                foreach (var column in VisibleColumns)
                {
                    builder.AppendLine($"\t{column.GraphQlName} : {joinTable.ColumnFilterTypeName}");
                }

                builder.AppendLine($"and: [{_table.GetJoinTypeName(joinTable)}!]");
                builder.AppendLine($"or: [{_table.GetJoinTypeName(joinTable)}!]");

                foreach (var link in _table.SingleLinks)
                {
                    if (IsEnumColumnLink(link.Value)) continue;
                    builder.AppendLine($"\t{link.Value.ParentTable.GraphQlName} : {_table.GetJoinTypeName(link.Value.ParentTable)}");
                }
                builder.AppendLine("}");
            }

            return builder.ToString();
        }
        public string GetAggregateLinkDefinitions()
        {
            var builder = new StringBuilder();
            builder.AppendLine($"input {_table.AggregateValueTypeName} {{");
            builder.AppendLine($"column : {_table.ColumnEnumTypeName}");
            // Self-FK tables have a single-link and multi-link both keyed by the
            // same GraphQlName; dedupe so we don't re-register the same field.
            var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var link in _table.MultiLinks)
            {
                //For multi-links _table is the ParentTable
                var fieldName = link.Value.ChildFieldName;
                if (!emitted.Add(fieldName)) continue;
                builder.AppendLine($"\t{fieldName} : {link.Value.ChildTable.AggregateValueTypeName}");
            }
            foreach (var link in _table.SingleLinks)
            {
                //For single links _table is the ChildTable
                if (IsEnumColumnLink(link.Value)) continue;
                var fieldName = link.Value.ParentFieldName;
                if (!emitted.Add(fieldName)) continue;
                builder.AppendLine($"\t{fieldName} : {link.Value.ParentTable.AggregateValueTypeName}");
            }
            builder.AppendLine("}");

            return builder.ToString();
        }

        public string GetTableFilterDefinition()
        {
            StringBuilder builder = new StringBuilder();
            builder.AppendLine($"input {_table.TableFilterTypeName} {{");
            foreach (var column in VisibleColumns)
            {
                var filterType = ResolveFieldType(column, false, FieldTypeKind.Filter);
                builder.AppendLine($"\t{column.GraphQlName} : {filterType}");
            }
            foreach (var link in _table.SingleLinks)
            {
                if (IsEnumColumnLink(link.Value)) continue;
                builder.AppendLine($"\t{link.Value.ParentTable.GraphQlName} : {link.Value.ParentTable.TableFilterTypeName}");
            }
            builder.AppendLine($"and: [{_table.TableFilterTypeName}!]");
            builder.AppendLine($"or: [{_table.TableFilterTypeName}!]");
            builder.AppendLine("}");

            return builder.ToString();
        }

        public string GetInputFieldDefinition()
        {
            var result = new StringBuilder();

            result.AppendLine(
                $"\t{_table.GraphQlName}(insert: {_table.GetActionTypeName(MutateActions.Insert)}, update: {_table.GetActionTypeName(MutateActions.Update)}, upsert: {_table.GetActionTypeName(MutateActions.Upsert)}, delete: {_table.GetActionTypeName(MutateActions.Delete)}, sync: {NestedSyncInsertTypeName}, _primaryKey: [String]{Modules.ModuleApiRegistry.MutationArgumentsSdl(_table)}) : Int");

            result.AppendLine($"{_table.GraphQlName}_batch(actions: [batch_{_table.GraphQlName}!]!{Modules.ModuleApiRegistry.MutationArgumentsSdl(_table)}) : Int");
            return result.ToString();
        }

        public string GetTableJoinType()
        {
            return SchemaGenerator.GetOnType(_table);
        }
    }
}
