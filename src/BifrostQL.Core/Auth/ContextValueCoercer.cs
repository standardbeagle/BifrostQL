using System.Globalization;
using BifrostQL.Core;
using BifrostQL.Core.Model;
using BifrostQL.Core.Utils;

namespace BifrostQL.Core.Auth;

/// <summary>
/// Converts a context claim, a policy literal or a key component to the CLR type of the
/// column it is compared with. The column's type family comes from the model's dialect
/// <see cref="ITypeMapper"/> — the same judgement the GraphQL schema is generated from —
/// so a type the dialect calls <c>Int</c> (Postgres <c>int4</c>, MySQL <c>int unsigned</c>)
/// or <c>Decimal</c> (SQL Server <c>money</c>) coerces as one here too. Before, this class
/// kept its own name table, which read <c>int4</c> as text and bound a string parameter
/// against an integer column. The one discriminator the mappers do not carry is uuid
/// (every dialect maps it to <c>String</c>), recognised by name below.
/// </summary>
internal static class ContextValueCoercer
{
    public static object Coerce(ITypeMapper typeMapper, IDbTable table, string columnName, object value)
    {
        var column = table.Columns.FirstOrDefault(c =>
            string.Equals(c.ColumnName, columnName, StringComparison.OrdinalIgnoreCase));
        if (column is null)
            throw AccessDenied();

        if (value is Array array)
        {
            if (array.Length != 1)
                throw AccessDenied();
            value = array.GetValue(0)!;
        }

        try
        {
            return ConvertToClrType(typeMapper, value, column);
        }
        catch (Exception ex) when (IsCoercionFailure(ex))
        {
            throw AccessDenied();
        }
    }

    /// <summary>
    /// Load-time probe: can <paramref name="value"/> be converted to <paramref name="column"/>'s
    /// type by the same conversion the runtime uses?
    /// </summary>
    internal static bool IsCoercible(ITypeMapper typeMapper, object value, ColumnDto column)
    {
        try
        {
            ConvertToClrType(typeMapper, value, column);
            return true;
        }
        catch (Exception ex) when (IsCoercionFailure(ex))
        {
            return false;
        }
    }

    private static bool IsCoercionFailure(Exception ex) =>
        ex is FormatException or InvalidCastException or OverflowException or ArgumentException;

    internal static object ConvertToClrType(ITypeMapper typeMapper, object value, ColumnDto column)
    {
        var clrType = GetClrType(typeMapper, column);
        if (clrType == typeof(Guid))
            return value is Guid guid ? guid : Guid.Parse(value.ToString()!);

        return Convert.ChangeType(value, clrType, CultureInfo.InvariantCulture)!;
    }

    internal static Type GetClrType(ITypeMapper typeMapper, ColumnDto column)
    {
        var dataType = column.EffectiveDataType;
        if (StringNormalizer.NormalizeType(dataType) is "uniqueidentifier" or "uuid")
            return typeof(Guid);

        return typeMapper.GetGraphQlType(dataType) switch
        {
            "Int" => typeof(int),
            "BigInt" => typeof(long),
            "Short" => typeof(short),
            "Byte" => typeof(byte),
            "Decimal" => typeof(decimal),
            "Float" => typeof(double),
            "Boolean" => typeof(bool),
            _ => typeof(string),
        };
    }

    private static Resolvers.BifrostExecutionError AccessDenied() =>
        new("Tenant context value is invalid for the target column.")
        { ErrorCode = Resolvers.BifrostExecutionError.AccessDeniedCode };
}
