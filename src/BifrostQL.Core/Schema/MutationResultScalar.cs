using System.Globalization;
using BifrostQL.Core.Model;

namespace BifrostQL.Core.Schema;

/// <summary>
/// Decides ONE thing in one place: the scalar a table's mutation field declares,
/// and the CLR value that field must hand back so that scalar can serialize it.
///
/// A table's mutation field is shared by insert/update/upsert/delete, and it carries
/// two different meanings: <see cref="Resolvers.TableMutationPipeline"/> returns the
/// affected row's KEY for a single-key table, and an affected-row COUNT for a delete
/// or a composite key. Declaring `Int` for every table therefore threw while
/// SERIALIZING whenever the key was not an int — after the write had already
/// committed, so the caller saw an error for a change that had in fact been made.
///
/// The declaration and the coercion have to agree, and the only way to guarantee
/// that is to derive both from the same rule: <see cref="Name"/> picks the scalar,
/// <see cref="Coerce"/> converts whatever the pipeline returned into a value that
/// scalar accepts. Split across two files they would drift, and the drift is
/// invisible until a table with an unusual key type is written to — which is
/// exactly how the original bug survived.
/// </summary>
public static class MutationResultScalar
{
    /// <summary>The GraphQL scalar name for this table's mutation field.</summary>
    public static string Name(IDbTable table, ITypeMapper typeMapper)
    {
        var keys = table.KeyColumns.ToList();
        // A composite key has no single scalar to return, so the pipeline answers
        // with an affected-row count.
        if (keys.Count != 1) return "Int";

        return typeMapper.GetGraphQlType(keys[0].EffectiveDataType) switch
        {
            // Short/Byte widen into Int32 losslessly, so they keep the historical Int
            // rather than churning the schema for no behavioural gain.
            "Int" or "Short" or "Byte" => "Int",
            "BigInt" => "BigInt",
            "Decimal" => "Decimal",
            "Float" => "Float",
            // String, DateTime, DateTimeOffset, uniqueidentifier-as-String… text is
            // the only scalar that can carry them all.
            _ => "String",
        };
    }

    /// <summary>
    /// Converts a pipeline result into a value the scalar from <see cref="Name"/>
    /// can serialize. This is what keeps DELETE working on a non-int-keyed table:
    /// its count is an <see cref="int"/>, which a String or Decimal field would
    /// otherwise refuse — the same class of after-the-write failure, just moved.
    /// </summary>
    public static object? Coerce(object? value, string scalarName)
    {
        if (value is null || value is DBNull) return null;

        return scalarName switch
        {
            "String" => value as string ?? Convert.ToString(value, CultureInfo.InvariantCulture),
            "Decimal" => Convert.ToDecimal(value, CultureInfo.InvariantCulture),
            "Float" => Convert.ToDouble(value, CultureInfo.InvariantCulture),
            // BigInt/Int accept the integral types the pipeline produces (a driver's
            // last-insert-id is commonly Int64) without conversion.
            _ => value,
        };
    }
}
