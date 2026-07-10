using BifrostQL.Core.Model;

namespace BifrostQL.Core.QueryModel
{
    /// <summary>
    /// Bundles the three collaborators that were previously hand-threaded through
    /// every SQL-building method on <see cref="GqlObjectQuery"/> and
    /// <see cref="TableFilter"/> — the schema <see cref="Model"/> (table/column
    /// allow-list), the active <see cref="Dialect"/> (identifier quoting,
    /// pagination, operators), and the <see cref="Parameters"/> collection that
    /// collects every bound value. Threading one object instead of the
    /// <c>(IDbModel, ISqlDialect, SqlParameterCollection)</c> data clump keeps the
    /// signatures small; it carries no per-call state (aliases stay explicit
    /// arguments), so a single instance is reused across a whole build.
    /// Mirrors the role of <c>VisualQueryBuilder.BuildContext</c>.
    /// </summary>
    public sealed class SqlBuildContext
    {
        public IDbModel Model { get; }
        public ISqlDialect Dialect { get; }
        public SqlParameterCollection Parameters { get; }

        public SqlBuildContext(IDbModel model, ISqlDialect dialect, SqlParameterCollection parameters)
        {
            Model = model;
            Dialect = dialect;
            Parameters = parameters;
        }
    }
}
