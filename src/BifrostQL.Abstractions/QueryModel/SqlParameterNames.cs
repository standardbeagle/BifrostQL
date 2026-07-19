using System.Text;

namespace BifrostQL.Core.QueryModel;

/// <summary>
/// Produces provider-safe parameter names from database column names. Mutation
/// SQL binds one parameter per written column and historically used the raw
/// column name (<c>@Order Date</c> for a column named "Order Date"), which is
/// invalid for every ADO provider and made all writes to such a table throw.
/// Both sides of the contract — the placeholder rendered into the SQL text
/// (<see cref="ISqlDialect.AssignmentPlaceholder"/>) and the bound
/// <c>DbParameter.ParameterName</c> (DbParameterBinder.AddParameters) — must
/// route through <see cref="Sanitize"/> so they always agree.
/// </summary>
public static class SqlParameterNames
{
    /// <summary>
    /// Returns <paramref name="columnName"/> unchanged when it is already a
    /// valid parameter name (<c>[A-Za-z_][A-Za-z0-9_]*</c>). Otherwise replaces
    /// every invalid character with <c>_</c> and appends an 8-hex-digit FNV-1a
    /// hash of the original name, so distinct originals that sanitize to the
    /// same base ("Order Date" vs "Order_Date") cannot collide, and the result
    /// is deterministic across the SQL-text and parameter-binding call sites.
    /// </summary>
    public static string Sanitize(string columnName)
    {
        ArgumentNullException.ThrowIfNull(columnName);
        if (IsValid(columnName))
            return columnName;

        var sb = new StringBuilder(columnName.Length + 10);
        foreach (var ch in columnName)
            sb.Append(IsValidChar(ch) ? ch : '_');
        if (sb.Length == 0 || char.IsAsciiDigit(sb[0]))
            sb.Insert(0, 'p');

        sb.Append('_').Append(Fnv1a(columnName).ToString("x8"));
        return sb.ToString();
    }

    private static bool IsValid(string name)
    {
        if (name.Length == 0 || char.IsAsciiDigit(name[0]))
            return false;
        foreach (var ch in name)
        {
            if (!IsValidChar(ch))
                return false;
        }
        return true;
    }

    private static bool IsValidChar(char ch) =>
        ch is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_';

    private static uint Fnv1a(string value)
    {
        var hash = 2166136261u;
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            hash ^= b;
            hash *= 16777619u;
        }
        return hash;
    }
}
