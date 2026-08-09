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
///
/// <para>This type also owns the OTHER parameter namespace bound onto the same
/// command: the generated <c>@p0/@p1/…</c> names produced by
/// <see cref="SqlParameterCollection"/> for transformer-injected predicates
/// (tenant scoping, soft-delete, policy). The two namespaces must be disjoint.
/// They were not: a column literally named <c>p0</c> is a legal parameter
/// identifier, so <see cref="Sanitize"/> returned it unchanged and the rendered
/// SQL carried a single <c>@p0</c> placeholder serving BOTH the client's column
/// assignment and the tenant predicate — the client's value silently became the
/// tenant predicate's value, i.e. a cross-tenant read/write. The generated shape
/// (<c>p</c> followed only by digits) is therefore RESERVED: a column name in
/// that shape is treated as needing sanitization and comes back hash-suffixed
/// (<c>p0</c> → <c>p0_1c4f2a9b</c>), which can never equal a generated name.
/// Reservation is case-insensitive because several ADO providers compare
/// parameter names case-insensitively.</para>
/// </summary>
public static class SqlParameterNames
{
    /// <summary>
    /// The generated name for a positional bound value, owned by
    /// <see cref="SqlParameterCollection"/>. Callers that hand-roll a positional
    /// parameter namespace must use this, so the reservation in
    /// <see cref="Sanitize"/> covers them too.
    /// </summary>
    public static string Generated(int index) => "p" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// True when <paramref name="name"/> occupies the reserved generated shape
    /// (<c>p</c> followed by one or more digits, case-insensitive) and so must
    /// never be produced from a column name.
    /// </summary>
    public static bool IsGeneratedShape(string name)
    {
        if (name.Length < 2 || (name[0] != 'p' && name[0] != 'P'))
            return false;
        for (var i = 1; i < name.Length; i++)
        {
            if (!char.IsAsciiDigit(name[i]))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Returns <paramref name="columnName"/> unchanged when it is already a
    /// valid parameter name (<c>[A-Za-z_][A-Za-z0-9_]*</c>) outside the reserved
    /// generated shape. Otherwise replaces every invalid character with <c>_</c>
    /// and appends an 8-hex-digit FNV-1a hash of the original name, so distinct
    /// originals that sanitize to the same base ("Order Date" vs "Order_Date")
    /// cannot collide, a reserved-shape name (<c>p0</c>) is pushed out of the
    /// generated namespace, and the result is deterministic across the SQL-text
    /// and parameter-binding call sites.
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
        // Reserved for SqlParameterCollection's generated names; see the type doc.
        if (IsGeneratedShape(name))
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
