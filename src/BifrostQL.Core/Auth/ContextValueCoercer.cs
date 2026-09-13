using System.Globalization;
using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;

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
            var target = GetClrType(column.DataType);
            return target == typeof(string) && value is string
                ? value
                : Convert.ChangeType(value, target, CultureInfo.InvariantCulture)!;
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException or ArgumentException)
        {
            throw AccessDenied();
        }
    }

    private static Type GetClrType(string dataType) =>
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

    private static BifrostExecutionError AccessDenied() =>
        new("Tenant context value is invalid for the target column.")
        { ErrorCode = BifrostExecutionError.AccessDeniedCode };
}
