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
                                // Extract numeric precision/scale from DECIMAL(10,2) style dbType
                                var (numericPrecision, numericScale) = ExtractNumericPrecision(c.DataType);
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
                                                 c.CompareMetadata("populate", "created-on") ||
                                                 c.CompareMetadata("populate", "created-by") ||
                                                 c.CompareMetadata("populate", "updated-on") ||
                                                 c.CompareMetadata("populate", "updated-by") ||
                                                 c.CompareMetadata("populate", "deleted-on") ||
                                                 c.CompareMetadata("populate", "deleted-by"),
                                    isCreatedOnColumn = c.CompareMetadata("populate", "created-on"),
                                    isCreatedByColumn = c.CompareMetadata("populate", "created-by"),
                                    isUpdatedOnColumn = c.CompareMetadata("populate", "updated-on"),
                                    isUpdatedByColumn = c.CompareMetadata("populate", "updated-by"),
                                    isDeletedOnColumn = c.CompareMetadata("populate", "deleted-on"),
                                    isDeletedColumn = c.CompareMetadata("populate", "deleted-by"),
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
                            multiJoins = t.MultiLinks.Values.Select(j => new
                            {
                                name = j.Name,
                                // fieldName is the GraphQL selection field on the source table;
                                // destinationTable remains the target table/type name.
                                fieldName = j.ChildFieldName,
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
                                sourceColumnNames = j.ChildIds.Select(c => c.GraphQlName).ToArray(),
                                destinationTable = j.ParentTable.GraphQlName,
                                destinationColumnNames = j.ParentIds.Select(p => p.GraphQlName).ToArray(),
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

        /// <summary>
        /// Extracts numeric precision and scale from data type strings like DECIMAL(10,2) or NUMERIC(18,4).
        /// Returns (precision, scale) as doubles for use as min/max, or nulls if not applicable.
        /// </summary>
        private static (double? precision, double? scale) ExtractNumericPrecision(string dataType)
        {
            if (string.IsNullOrEmpty(dataType))
                return (null, null);

            var upperType = dataType.ToUpperInvariant();

            // Only apply to decimal/numeric types
            if (!upperType.StartsWith("DECIMAL") && !upperType.StartsWith("NUMERIC") && !upperType.StartsWith("DEC"))
                return (null, null);

            var openParen = dataType.IndexOf('(');
            if (openParen < 0)
                return (null, null);

            var closeParen = dataType.IndexOf(')', openParen);
            if (closeParen < 0)
                return (null, null);

            var parts = dataType.Substring(openParen + 1, closeParen - openParen - 1).Split(',');
            if (parts.Length >= 2)
            {
                if (int.TryParse(parts[0].Trim(), out var precision) &&
                    int.TryParse(parts[1].Trim(), out var scale))
                {
                    // Return precision and scale as informational values
                    // These can be used by clients to understand the data type constraints
                    return (precision, scale);
                }
            }

            return (null, null);
        }
    }
}
