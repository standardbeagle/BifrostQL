using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;

namespace BifrostQL.Server.Grpc
{
    /// <summary>
    /// The gRPC-specific companions to the shared <see cref="SchemaReadVisibility"/> projection.
    /// The per-identity read projection itself — which tables and columns a caller may see in a
    /// descriptor / <c>.proto</c> / descriptor-set artifact — is <see cref="SchemaReadVisibility"/>
    /// and is NOT re-implemented here (.claude/rules/protocol-adapter-security.md invariant 4).
    /// </summary>
    public static class GrpcSchemaVisibility
    {
        /// <summary>
        /// Every table with all its columns, with NO policy filtering. This is used ONLY to build
        /// the runtime DISPATCH method/routing table (which method names exist) and the shared field
        /// numbering, never to decide what a caller may read — authorization is enforced per call by
        /// the transformer pipeline, and per-identity REFLECTION uses
        /// <see cref="SchemaReadVisibility.Project"/>. So a table nobody may read still gets a route,
        /// but every call to it is scoped away and the route is never advertised in reflection.
        /// </summary>
        public static IReadOnlyList<VisibleTable> ProjectAll(IDbModel model)
        {
            if (model is null) throw new ArgumentNullException(nameof(model));
            return model.Tables
                .Select(t => new VisibleTable(t, t.Columns.ToList()))
                .ToList();
        }

        /// <summary>
        /// The columns of a SINGLE table the identity may READ. Used by the read compiler to
        /// validate filter/sort field names against what the caller can actually see, so filtering
        /// on a hidden column is rejected rather than becoming an existence oracle (invariant 4).
        /// A table the caller cannot read yields no columns (fail closed).
        /// </summary>
        public static IReadOnlyList<ColumnDto> VisibleReadColumns(
            IDbTable table, IDictionary<string, object?> userContext) =>
            SchemaReadVisibility.ProjectTable(table, userContext)?.Columns ?? Array.Empty<ColumnDto>();
    }
}
