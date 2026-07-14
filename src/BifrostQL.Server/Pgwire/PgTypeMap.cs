using BifrostQL.Core.Utils;

namespace BifrostQL.Server.Pgwire
{
    /// <summary>
    /// A PostgreSQL result-column type as it appears in a RowDescription field: the
    /// type's OID plus its on-wire type length (<c>-1</c> for variable-length types).
    /// The type modifier (typmod) is always <c>-1</c> in slice 2 — Bifrost does not
    /// carry column length/precision through to the wire yet.
    /// </summary>
    internal readonly record struct PgType(int Oid, short TypeLength);

    /// <summary>
    /// One result column advertised in a RowDescription: the column's wire name (the same
    /// key the executed row dictionary uses, so values decode positionally) and its
    /// Bifrost/SQL data-type string, which <see cref="PgTypeMap"/> maps to a pg type OID.
    /// </summary>
    internal readonly record struct PgResultColumn(string Name, string DataType);

    /// <summary>
    /// Maps a Bifrost/SQL column data-type name to the PostgreSQL type the simple query
    /// protocol advertises for it. Covers the common types the query path round-trips;
    /// anything unrecognized falls back to <c>text</c> (OID 25), which every pg client
    /// can decode from the text representation the value encoder produces.
    ///
    /// <para>OID values are the fixed catalog OIDs from <c>pg_type</c>; they are protocol
    /// constants, not database-specific.</para>
    /// </summary>
    internal static class PgTypeMap
    {
        public const int OidBool = 16;       // bool, 1 byte
        public const int OidInt8 = 20;       // bigint, 8 bytes
        public const int OidInt2 = 21;       // smallint, 2 bytes
        public const int OidInt4 = 23;       // int, 4 bytes
        public const int OidText = 25;       // text, variable
        public const int OidFloat4 = 700;    // real, 4 bytes
        public const int OidFloat8 = 701;    // double precision, 8 bytes
        public const int OidVarchar = 1043;  // varchar, variable
        public const int OidDate = 1082;     // date, 4 bytes
        public const int OidTimestamp = 1114; // timestamp without time zone, 8 bytes
        public const int OidNumeric = 1700;  // numeric/decimal, variable
        public const int OidUuid = 2950;     // uuid, 16 bytes

        /// <summary>The typmod advertised for every column in slice 2 (no modifier).</summary>
        public const int NoTypeModifier = -1;

        private const short Variable = -1;

        /// <summary>
        /// Resolves the pg type for a column data-type string (e.g. <c>"varchar(50)"</c>,
        /// <c>"bigint"</c>, <c>"uniqueidentifier"</c>). The name is lower-cased and any
        /// <c>(length)</c>/<c>(precision,scale)</c> suffix is stripped before matching, so
        /// dialect-decorated types map the same as their bare form. Unknown types → text.
        /// </summary>
        public static PgType Map(string? dataType)
        {
            var normalized = StripLengthSpec(StringNormalizer.NormalizeType(dataType));
            return normalized switch
            {
                "bool" or "boolean" or "bit" => new PgType(OidBool, 1),

                "bigint" or "int8" or "long" => new PgType(OidInt8, 8),
                "smallint" or "int2" or "tinyint" => new PgType(OidInt2, 2),
                "int" or "integer" or "int4" or "int32" => new PgType(OidInt4, 4),

                "real" or "float4" or "single" => new PgType(OidFloat4, 4),
                "float" or "float8" or "double" or "double precision" => new PgType(OidFloat8, 8),

                "decimal" or "numeric" or "money" => new PgType(OidNumeric, Variable),

                "uuid" or "uniqueidentifier" or "guid" => new PgType(OidUuid, 16),

                "date" => new PgType(OidDate, 4),
                "datetime" or "datetime2" or "smalldatetime" or "timestamp" => new PgType(OidTimestamp, 8),

                "varchar" or "nvarchar" or "char" or "nchar" => new PgType(OidVarchar, Variable),

                // text/ntext/clob/xml/json and everything unrecognized: text.
                _ => new PgType(OidText, Variable),
            };
        }

        /// <summary>Strips a trailing <c>(...)</c> length/precision decoration, if present.</summary>
        private static string StripLengthSpec(string type)
        {
            var paren = type.IndexOf('(');
            return paren < 0 ? type : type[..paren].Trim();
        }
    }
}
