using GraphQL.Resolvers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BifrostQL.Core.Model;
using GraphQL;
using System.Data.Common;
using BifrostQL.Core.Schema;

namespace BifrostQL.Core.Resolvers
{
    public interface IDbSchemaResolver : IBifrostResolver, IFieldResolver
    {

    }

    public class MetaSchemaResolver : IDbSchemaResolver
    {
        private readonly IDbModel _dbModel;
        public MetaSchemaResolver(IDbModel dbModel)
        {
            _dbModel = dbModel;
        }

        public ValueTask<object?> ResolveAsync(IBifrostFieldContext context)
        {
            var tableName = context.GetArgument<string?>("graphQlName");
            return ValueTask.FromResult<object?>(
                _dbModel.Tables
                    .Where(t => tableName == null || t.GraphQlName == tableName)
                    .Select(t =>
                    {
                        var labelColumnName = t.GetMetadataValue(MetadataKeys.Ui.Label);
                        var labelColumn = t.Columns.FirstOrDefault(c => Equal(c.DbName, labelColumnName));
                        if (labelColumn == null && t.KeyColumns.Any())
                        {
                            var detected = LookupTableDetector.DetectColumnRoles(t).LabelColumn;
                            labelColumn = t.Columns.FirstOrDefault(c => Equal(c.ColumnName, detected));
                        }
                        labelColumn ??= t.Columns.First();
                        return new
                        {
                            Schema = t.TableSchema,
                            t.DbName,
                            t.GraphQlName,
                            labelColumn = labelColumn.GraphQlName,
                            primaryKeys = t.Columns.Where(c => c.IsPrimaryKey == true).Select(pk => pk.GraphQlName),
                            isEditable = t.Columns.Any(c => c.IsPrimaryKey == true),
                            metadata = t.Metadata,
                            columns = t.Columns
                                .Where(c => !c.CompareMetadata(MetadataKeys.Ui.Visibility, MetadataKeys.Ui.Hidden))
                                .Select(c =>
                            {
                                // Effective declarative validation rules — same derivation the
                                // server-side validator uses, so clients can mirror enforcement.
                                var rules = Modules.Validation.ValidationRules.ForColumn(c);
                                // Schema-captured precision/scale (INFORMATION_SCHEMA or the
                                // SQLite declared type) — the same facts server validation
                                // enforces, so clients mirror exactly what the server refuses.
                                var (numericPrecision, numericScale) = ((double?)rules.NumericPrecision, (double?)rules.NumericScale);
                                // Get enum values from metadata if present
                                var enumValues = c.GetMetadataValue(MetadataKeys.Enum.Values)?.Split(',').Select(v => v.Trim()).ToArray();
                                var enumLabels = c.GetMetadataValue(MetadataKeys.Enum.Labels)?.Split(',').Select(v => v.Trim()).ToArray();
                                // Labels map to values positionally, so a count mismatch would shift
                                // every label onto the wrong value. Drop the labels in that case and
                                // let the client fall back to the raw values rather than mislabel them.
                                if (enumLabels != null && (enumValues == null || enumLabels.Length != enumValues.Length))
                                    enumLabels = null;

                                return new
                                {
                                    dbName = c.DbName,
                                    graphQlName = c.GraphQlName,
                                    paramType = SchemaGenerator.GetGraphQlTypeName(c.EffectiveDataType, c.IsNullable, _dbModel.TypeMapper),
                                    dbType = c.DataType,
                                    isNullable = c.IsNullable,
                                    isPrimaryKey = c.IsPrimaryKey,
                                    isUnique = c.IsUnique,
                                    isIdentity = c.IsIdentity,
                                    isReadOnly = c.IsPrimaryKey || c.IsIdentity || c.IsComputed ||
                                                 c.CompareMetadata(MetadataKeys.AutoPopulate.Marker, MetadataKeys.AutoPopulate.CreatedOn) ||
                                                 c.CompareMetadata(MetadataKeys.AutoPopulate.Marker, MetadataKeys.AutoPopulate.CreatedBy) ||
                                                 c.CompareMetadata(MetadataKeys.AutoPopulate.Marker, MetadataKeys.AutoPopulate.UpdatedOn) ||
                                                 c.CompareMetadata(MetadataKeys.AutoPopulate.Marker, MetadataKeys.AutoPopulate.UpdatedBy) ||
                                                 c.CompareMetadata(MetadataKeys.AutoPopulate.Marker, MetadataKeys.AutoPopulate.DeletedOn) ||
                                                 c.CompareMetadata(MetadataKeys.AutoPopulate.Marker, MetadataKeys.AutoPopulate.DeletedBy),
                                    isCreatedOnColumn = c.CompareMetadata(MetadataKeys.AutoPopulate.Marker, MetadataKeys.AutoPopulate.CreatedOn),
                                    isCreatedByColumn = c.CompareMetadata(MetadataKeys.AutoPopulate.Marker, MetadataKeys.AutoPopulate.CreatedBy),
                                    isUpdatedOnColumn = c.CompareMetadata(MetadataKeys.AutoPopulate.Marker, MetadataKeys.AutoPopulate.UpdatedOn),
                                    isUpdatedByColumn = c.CompareMetadata(MetadataKeys.AutoPopulate.Marker, MetadataKeys.AutoPopulate.UpdatedBy),
                                    isDeletedOnColumn = c.CompareMetadata(MetadataKeys.AutoPopulate.Marker, MetadataKeys.AutoPopulate.DeletedOn),
                                    isDeletedColumn = c.CompareMetadata(MetadataKeys.AutoPopulate.Marker, MetadataKeys.AutoPopulate.DeletedBy),
                                    isLargeValue = _dbModel.TypeMapper.IsLargeValue(c.DataType),
                                    maxLength = rules.MaxLength,
                                    minLength = rules.MinLength,
                                    min = rules.Min,
                                    max = rules.Max,
                                    step = rules.Step,
                                    required = rules.Required,
                                    precision = numericPrecision,
                                    scale = numericScale,
                                    pattern = rules.Pattern,
                                    patternMessage = rules.PatternMessage ?? c.GetMetadataValue(MetadataKeys.DataType.Title),
                                    inputType = rules.InputType,
                                    defaultValue = c.GetMetadataValue(MetadataKeys.DataType.Default),
                                    enumValues,
                                    enumLabels,
                                    metadata = c.Metadata
                                };
                            }),
                            // Index columns are translated to GraphQL names so clients
                            // can match them against the columns list / sort enums
                            // directly. An index whose key includes a column the model
                            // does not expose (hidden or filtered) is omitted: a client
                            // cannot sort by a column it cannot see, so a partial
                            // column list would misrepresent the access path.
                            indexes = t.Indexes
                                .Select(ix => new
                                {
                                    name = ix.Name,
                                    isUnique = ix.IsUnique,
                                    isClustered = ix.IsClustered,
                                    isPrimaryKey = ix.IsPrimaryKey,
                                    columns = ix.ColumnNames
                                        .Select(n => t.Columns.FirstOrDefault(c => Equal(c.DbName, n))?.GraphQlName)
                                        .ToArray(),
                                })
                                .Where(ix => ix.columns.All(c => c != null))
                                .Select(ix => new { ix.name, ix.isUnique, ix.isClustered, ix.isPrimaryKey, columns = ix.columns.Cast<string>().ToArray() }),
                            multiJoins = t.MultiLinks.Values.Select(j => new
                            {
                                name = j.Name,
                                // fieldName is the GraphQL selection field on the source table;
                                // destinationTable remains the target table/type name.
                                fieldName = j.ChildFieldName,
                                relationshipKind = RelationshipKindValue(j.RelationshipKind),
                                sourceColumnNames = j.ParentIds.Select(p => p.GraphQlName).ToArray(),
                                destinationTable = j.ChildTable.GraphQlName,
                                destinationColumnNames = j.ChildIds.Select(c => c.GraphQlName).ToArray(),
                                // Polymorphic child links carry a discriminator predicate so
                                // the UI can badge them and skip treating them as plain FKs.
                                isPolymorphic = j.TypePredicate != null,
                                polymorphicTypeColumn = j.TypePredicate?.Column.GraphQlName,
                                polymorphicTypeValue = j.TypePredicate?.Value?.ToString(),
                            }),
                            singleJoins = t.SingleLinks.Values.Select(j => new
                            {
                                name = j.Name,
                                // fieldName is the GraphQL selection field on the source table;
                                // destinationTable remains the target table/type name.
                                fieldName = j.ParentFieldName,
                                relationshipKind = RelationshipKindValue(j.RelationshipKind),
                                sourceColumnNames = j.ChildIds.Select(c => c.GraphQlName).ToArray(),
                                destinationTable = j.ParentTable.GraphQlName,
                                destinationColumnNames = j.ParentIds.Select(p => p.GraphQlName).ToArray(),
                                // dbJoinSchema backs both join lists, so the discriminator
                                // fields must be projected here too: a field the SDL
                                // declares but the projection omits fails the whole query.
                                isPolymorphic = j.TypePredicate != null,
                                polymorphicTypeColumn = j.TypePredicate?.Column.GraphQlName,
                                polymorphicTypeValue = j.TypePredicate?.Value?.ToString(),
                            }),
                            // Many-to-many bridges. The UI uses the junction's MultiLink for
                            // the rows query and these fields to skip to the target entity:
                            // junctionTargetField is the selection on the junction type that
                            // resolves the target row; hasPayload marks junctions carrying
                            // extra columns the UI can reveal.
                            manyToManyJoins = t.ManyToManyLinks.Values.Select(m => new
                            {
                                name = m.JunctionTable.GraphQlName,
                                targetTable = m.TargetTable.GraphQlName,
                                junctionTable = m.JunctionTable.GraphQlName,
                                junctionTargetField =
                                    m.JunctionTable.SingleLinks.TryGetValue(m.TargetTable.GraphQlName, out var tl)
                                        ? tl.ParentFieldName
                                        : m.TargetTable.GraphQlName,
                                sourceColumnNames = new[] { m.SourceColumn.GraphQlName },
                                junctionSourceColumnNames = new[] { m.JunctionSourceColumn.GraphQlName },
                                junctionTargetColumnNames = new[] { m.JunctionTargetColumn.GraphQlName },
                                targetColumnNames = new[] { m.TargetColumn.GraphQlName },
                                hasPayload = m.HasPayload,
                            })
                        };
                    })
            );
        }
        ValueTask<object?> IFieldResolver.ResolveAsync(IResolveFieldContext context)
        {
            return ResolveAsync(new BifrostFieldContextAdapter(context));
        }

        static bool Equal(string? a, string? b) => string.Equals(a, b, StringComparison.InvariantCultureIgnoreCase);

        private static string RelationshipKindValue(TableLinkRelationshipKind kind) => kind switch
        {
            TableLinkRelationshipKind.ForeignKey => "foreign-key",
            TableLinkRelationshipKind.NameBased => "name-based",
            TableLinkRelationshipKind.Polymorphic => "polymorphic",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown table-link relationship kind."),
        };

    }
}
