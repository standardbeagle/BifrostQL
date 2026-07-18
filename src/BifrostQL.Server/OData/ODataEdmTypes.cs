using BifrostQL.Core.Model;

namespace BifrostQL.Server.OData
{
    /// <summary>
    /// Deterministic mapping from a database scalar column to its OData v4 EDM primitive type.
    /// Reuses the model's own <see cref="ITypeMapper"/> (the same dialect-aware mapper the
    /// GraphQL schema is built from) to normalize the raw database type first, then maps that
    /// normalized name to the canonical <c>Edm.*</c> primitive. Every unrecognized type falls
    /// back to <c>Edm.String</c>, exactly as the type mapper falls back to GraphQL <c>String</c>,
    /// so the projection never fails on an exotic column type.
    /// </summary>
    internal static class ODataEdmTypes
    {
        /// <summary>
        /// Returns the <c>Edm.*</c> primitive type name for <paramref name="column"/> using
        /// <paramref name="typeMapper"/> to normalize the database type. OData v4 has no plain
        /// date-time type, so both <c>DateTime</c> and <c>DateTimeOffset</c> map to
        /// <c>Edm.DateTimeOffset</c>, its canonical instant type.
        /// </summary>
        public static string ForColumn(ColumnDto column, ITypeMapper typeMapper)
        {
            if (column is null) throw new ArgumentNullException(nameof(column));
            if (typeMapper is null) throw new ArgumentNullException(nameof(typeMapper));

            return typeMapper.GetGraphQlType(column.EffectiveDataType) switch
            {
                "Int" => "Edm.Int32",
                "Short" => "Edm.Int16",
                "Byte" => "Edm.Byte",
                "BigInt" => "Edm.Int64",
                "Decimal" => "Edm.Decimal",
                "Float" => "Edm.Double",
                "Boolean" => "Edm.Boolean",
                "DateTime" => "Edm.DateTimeOffset",
                "DateTimeOffset" => "Edm.DateTimeOffset",
                // JSON and every other unrecognized type project as a string payload.
                _ => "Edm.String",
            };
        }
    }
}
