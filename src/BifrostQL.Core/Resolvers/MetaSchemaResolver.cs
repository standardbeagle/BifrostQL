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
                            columns = t.Columns.Select(c =>
                            {
                                // Extract maxLength from VARCHAR(255) or NVARCHAR(100) style dbType
                                int? maxLength = ExtractMaxLength(c.DataType);
                                // Extract numeric precision/scale from DECIMAL(10,2) style dbType
                                var (numericPrecision, numericScale) = ExtractNumericPrecision(c.DataType);
                                // Get enum values from metadata if present
                                var enumValues = c.GetMetadataValue(MetadataKeys.Enum.Values)?.Split(',').Select(v => v.Trim()).ToArray();
                                var enumLabels = c.GetMetadataValue(MetadataKeys.Enum.Labels)?.Split(',').Select(v => v.Trim()).ToArray();

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
                                    maxLength,
                                    minLength = (int?)null,
                                    min = numericPrecision,
                                    max = numericScale,
                                    step = (double?)null,
                                    pattern = c.GetMetadataValue(MetadataKeys.Validation.Pattern),
                                    patternMessage = c.GetMetadataValue(MetadataKeys.Validation.PatternMessage) ?? c.GetMetadataValue(MetadataKeys.DataType.Title),
                                    inputType = c.GetMetadataValue(MetadataKeys.Validation.InputType),
                                    defaultValue = c.GetMetadataValue(MetadataKeys.DataType.Default),
                                    enumValues,
                                    enumLabels,
                                    metadata = c.Metadata
                                };
                            }),
                            multiJoins = t.MultiLinks.Values.Select(j => new
                            {
                                name = j.Name,
                                sourceColumnNames = new[] { j.ParentId.GraphQlName },
                                destinationTable = j.ChildTable.GraphQlName,
                                destinationColumnNames = new[] { j.ChildId.GraphQlName },
                            }),
                            singleJoins = t.SingleLinks.Values.Select(j => new
                            {
                                name = j.Name,
                                sourceColumnNames = new[] { j.ChildId.GraphQlName },
                                destinationTable = j.ParentTable.GraphQlName,
                                destinationColumnNames = new[] { j.ParentId.GraphQlName },
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
        /// Extracts max length from data type strings like VARCHAR(255), NVARCHAR(100), CHAR(50).
        /// Returns null if no length is specified or if the type doesn't support length.
        /// </summary>
        private static int? ExtractMaxLength(string dataType)
        {
            if (string.IsNullOrEmpty(dataType))
                return null;

            // Look for pattern like VARCHAR(255) or NVARCHAR(max) - only extract if it's a number
            var openParen = dataType.IndexOf('(');
            if (openParen < 0)
                return null;

            var closeParen = dataType.IndexOf(')', openParen);
            if (closeParen < 0)
                return null;

            var lengthStr = dataType.Substring(openParen + 1, closeParen - openParen - 1).Trim();

            // Handle "max" case - return null as it doesn't represent a numeric constraint
            if (string.Equals(lengthStr, "max", StringComparison.OrdinalIgnoreCase))
                return null;

            if (int.TryParse(lengthStr, out var length) && length > 0)
                return length;

            return null;
        }

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
