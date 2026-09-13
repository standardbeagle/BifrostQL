using System.Globalization;
using BifrostQL.Core;
using BifrostQL.Core.Model;

namespace BifrostQL.Core.Auth;

internal static class ContextValueCoercer
{
    public static object Coerce(IDbTable table, string columnName, object value)
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
            return ConvertToClrType(value, column.DataType);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException or ArgumentException)
        {
            throw AccessDenied();
        }
    }

    internal static object ConvertToClrType(object value, string dataType)
    {
        var normalized = dataType.ToLowerInvariant();
        if (normalized is "uniqueidentifier" or "uuid")
            return value is Guid guid ? guid : Guid.Parse(value.ToString()!);

        return Convert.ChangeType(value, GetClrType(dataType), CultureInfo.InvariantCulture)!;
    }

    internal static Type GetClrType(string dataType) =>
        dataType.ToLowerInvariant() switch
        {
            "int" or "integer" => typeof(int),
            "bigint" => typeof(long),
            "smallint" => typeof(short),
            "tinyint" => typeof(byte),
            "decimal" or "numeric" => typeof(decimal),
            "float" or "real" => typeof(double),
            "uniqueidentifier" or "uuid" => typeof(Guid),
            _ => typeof(string)
        };

    private static Resolvers.BifrostExecutionError AccessDenied() =>
        new("Tenant context value is invalid for the target column.")
        { ErrorCode = Resolvers.BifrostExecutionError.AccessDeniedCode };
}
