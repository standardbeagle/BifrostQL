using BifrostQL.Core.Model;

namespace BifrostQL.Server.OData
{
    /// <summary>
    /// Resolves a caller-supplied OData property name against an entity's VISIBLE columns — the
    /// single, shared rule the read translator ($select/$orderby) and the $filter translator both
    /// use, so a name that is unknown or read-denied is rejected identically everywhere and never
    /// interpolated into a query. Matching is by EDM property name (case-insensitively); a name
    /// that matches more than one visible column is reported as ambiguous rather than silently
    /// resolved to one.
    /// </summary>
    internal static class ODataProperty
    {
        public static ColumnDto Resolve(ODataEntity entity, string name, string context)
        {
            var matches = entity.Columns
                .Where(c => string.Equals(c.GraphQlName, name, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matches.Count == 0)
                throw ODataProtocolException.BadRequest(
                    $"{context} references unknown property '{name}' on entity '{entity.Table.GraphQlName}'.");
            if (matches.Count > 1)
                throw ODataProtocolException.BadRequest(
                    $"{context} references ambiguous property '{name}' on entity '{entity.Table.GraphQlName}'.");
            return matches[0];
        }
    }
}
