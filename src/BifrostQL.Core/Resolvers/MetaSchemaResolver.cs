using GraphQL.Resolvers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using GraphQL;
using System.Data.Common;
using BifrostQL.Core.Schema;

namespace BifrostQL.Core.Resolvers
{
    public interface IDbSchemaResolver : IBifrostResolver, IFieldResolver
    {

    }

    /// <summary>
    /// Resolves <c>_dbSchema</c> PER CALLER: the model is projected through
    /// <see cref="SchemaReadVisibility.Project"/> (a table or column the caller may not
    /// read is absent — the same answer every other catalog gives), each table carries
    /// <c>allowedActions</c> resolved via <see cref="PolicyEvaluator.CanAct"/>, and each
    /// column carries <c>readable</c>/<c>writable</c> via
    /// <see cref="PolicyEvaluator.IsColumnAllowed"/>. The raw metadata bag (which contains
    /// the <c>policy-*</c> rules) is served to admin callers only. <c>isEditable</c> is
    /// retained for compatibility and means exactly "the table has a key" — it is NOT an
    /// authorization answer; clients must read <c>allowedActions</c> for that.
    /// </summary>
    public class MetaSchemaResolver : IDbSchemaResolver
    {
        private static readonly PolicyEvaluator Evaluator = new();
        private static readonly IDictionary<string, object?> EmptyMetadata =
            new Dictionary<string, object?>();

        private readonly IDbModel _dbModel;
        public MetaSchemaResolver(IDbModel dbModel)
        {
            _dbModel = dbModel;
        }

        public ValueTask<object?> ResolveAsync(IBifrostFieldContext context)
        {
            var tableName = context.GetArgument<string?>("graphQlName");
            var identity = PolicyIdentity.FromUserContext(context.UserContext);
            var isAdmin = identity.Grants.Contains(MetadataKeys.Policy.DefaultAdminRole);
            var visible = SchemaReadVisibility.Project(_dbModel, context.UserContext);
            return ValueTask.FromResult<object?>(
                visible
                    .Select(v => (v, t: v.Table))
                    .Where(x => tableName == null || x.t.GraphQlName == tableName)
                    .Select(x =>
                    {
                        var (v, t) = x;
                        var policy = PolicyConfigCollector.FromTable(t);
                        // Table actions, in the client's canonical order. D7 semantics:
                        // an unlisted action is denied to everyone when the policy lists
                        // any actions at all; the admin bypass covers grant requirements.
                        var allowedActions = new[]
                            {
                                (PolicyAction.Read, "read"),
                                (PolicyAction.Create, "create"),
                                (PolicyAction.Update, "update"),
                                (PolicyAction.Delete, "delete"),
                            }
                            .Where(a => Evaluator.CanAct(policy, a.Item1, identity).Allowed)
                            .Select(a => a.Item2)
                            .ToArray();
                        var labelColumnName = t.GetMetadataValue(MetadataKeys.Ui.Label);
                        var labelColumn = t.Columns.FirstOrDefault(c => Equal(c.DbName, labelColumnName));
                        if (labelColumn == null && t.KeyColumns.Any())
                        {
                            var detected = LookupTableDetector.DetectColumnRoles(t).LabelColumn;
                            labelColumn = t.Columns.FirstOrDefault(c => Equal(c.ColumnName, detected));
                        }
                         labelColumn ??= t.Columns.FirstOrDefault();
                        // A label column the caller may not read must not be named back
                        // to them — fall back to the first visible column.
                         if (labelColumn != null && !v.HasColumn(labelColumn.DbName))
                             labelColumn = v.Columns.FirstOrDefault();
                        return new
                        {
                            Schema = t.TableSchema,
                            t.DbName,
                            t.GraphQlName,
                             labelColumn = labelColumn?.GraphQlName,
                            primaryKeys = t.Columns
                                .Where(c => c.IsPrimaryKey == true && v.HasColumn(c.DbName))
                                .Select(pk => pk.GraphQlName),
                            isEditable = t.Columns.Any(c => c.IsPrimaryKey == true),
                            allowedActions,
                             metadata = isAdmin
                                 ? t.Metadata
                                 : t.Metadata
                                     .Where(kv => string.Equals(kv.Key, MetadataKeys.Ui.DisplayFormat, StringComparison.OrdinalIgnoreCase))
                                     .ToDictionary(kv => kv.Key, kv => kv.Value),
                            columns = v.Columns
                                 .Where(c => !c.CompareMetadata(MetadataKeys.Ui.Visibility, MetadataKeys.Ui.Hidden))
                                .Select(c =>
                            {
                                // S4a/S4b semantics. `readable` is false for a MASKED
                                // column too: the selection still succeeds with the value
                                // nulled, so the column stays listed and selectable, but
                                // the caller never sees its values. A refuse-denied column
                                // is absent from the projection entirely.
                                var readable =
                                    Evaluator.IsColumnAllowed(policy, c.DbName, PolicyDirection.Read, identity).Allowed
                                    && Evaluator.GetReadDisposition(policy, c.DbName, identity) == ReadColumnDisposition.Allow;
                                var writable =
                                    Evaluator.IsColumnAllowed(policy, c.DbName, PolicyDirection.Write, identity).Allowed;
                                // Effective declarative validation rules — same derivation the
                                // server-side validator uses, so clients can mirror enforcement.
                                var rules = Modules.Validation.ValidationRules.ForColumn(c);
                                // Schema-captured precision/scale (INFORMATION_SCHEMA or the
                                // SQLite declared type) — the same facts server validation
                                // enforces, so clients mirror exactly what the server refuses.
                                var (numericPrecision, numericScale) = ((double?)rules.NumericPrecision, (double?)rules.NumericScale);
                                // A temporal column with no declared min/max advertises the
                                // ENGINE's storable window (SQL Server datetime >= 1753,
                                // MySQL timestamp 1970–2038) so date pickers bound their
                                // range and the client refuses what the server would.
                                // Declared metadata always wins.
                                var (effectiveMin, effectiveMax) = (rules.Min, rules.Max);
                                if ((effectiveMin is null || effectiveMax is null)
                                    && rules.TemporalKind is not Modules.Validation.TemporalKind.None and not Modules.Validation.TemporalKind.TimeOnly
                                    && _dbModel.TypeMapper.GetTemporalRange(c.EffectiveDataType) is { } window)
                                {
                                    effectiveMin ??= window.Min.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
                                    effectiveMax ??= window.Max.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
                                }
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
                                    readable,
                                    writable,
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
                                    min = effectiveMin,
                                    max = effectiveMax,
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
                                     metadata = isAdmin
                                         ? c.Metadata
                                         : c.Metadata
                                             .Where(kv => string.Equals(kv.Key, MetadataKeys.Ui.DisplayFormat, StringComparison.OrdinalIgnoreCase))
                                             .ToDictionary(kv => kv.Key, kv => kv.Value)
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
                                        .Select(n => v.HasColumn(n)
                                            ? t.Columns.FirstOrDefault(c => Equal(c.DbName, n))?.GraphQlName
                                            : null)
                                        .ToArray(),
                                })
                                .Where(ix => ix.columns.All(c => c != null))
                                .Select(ix => new { ix.name, ix.isUnique, ix.isClustered, ix.isPrimaryKey, columns = ix.columns.Cast<string>().ToArray() }),
                            // An edge naming a table or column the caller cannot see
                            // re-discloses it — publish only edges whose BOTH ends are
                            // visible (same rule as every other catalog).
                            multiJoins = t.MultiLinks.Values
                                .Where(j => SchemaReadVisibility.IsLinkVisible(j, visible))
                                .Select(j => new
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
                            singleJoins = t.SingleLinks.Values
                                .Where(j => SchemaReadVisibility.IsLinkVisible(j, visible))
                                .Select(j => new
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
                            manyToManyJoins = t.ManyToManyLinks.Values
                                .Where(m => SchemaReadVisibility.Find(visible, m.JunctionTable) != null
                                    && SchemaReadVisibility.Find(visible, m.TargetTable) != null)
                                .Select(m => new
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

    /// <summary>
    /// <c>_grants: [String!]!</c> — the caller's own grant set: the union of roles and
    /// permissions projected by <see cref="PolicyIdentity.FromUserContext"/> (S1), sorted.
    /// The answer a client reads to decide what ITS user may do; it carries no other
    /// caller's grants and no model data.
    /// </summary>
    public sealed class CallerGrantsResolver : IBifrostResolver, IFieldResolver
    {
        public ValueTask<object?> ResolveAsync(IBifrostFieldContext context)
        {
            var grants = PolicyIdentity.FromUserContext(context.UserContext).Grants
                .OrderBy(g => g, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return ValueTask.FromResult<object?>(grants);
        }

        ValueTask<object?> IFieldResolver.ResolveAsync(IResolveFieldContext context)
            => ResolveAsync(new BifrostFieldContextAdapter(context));
    }

    /// <summary>
    /// <c>_policyGrants: [String!]!</c> — the catalogue of every grant name the model's
    /// policy metadata references (E18), sorted and de-duplicated. This is what an app's
    /// profile editor lists; it names grants, never which caller holds them.
    /// </summary>
    public sealed class PolicyGrantCatalogueResolver : IBifrostResolver, IFieldResolver
    {
        private readonly IDbModel _model;

        public PolicyGrantCatalogueResolver(IDbModel model)
        {
            _model = model ?? throw new ArgumentNullException(nameof(model));
        }

        public ValueTask<object?> ResolveAsync(IBifrostFieldContext context)
            => ValueTask.FromResult<object?>(PolicyConfigCollector.ReferencedGrants(_model));

        ValueTask<object?> IFieldResolver.ResolveAsync(IResolveFieldContext context)
            => ResolveAsync(new BifrostFieldContextAdapter(context));
    }
}
