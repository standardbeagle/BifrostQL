using System.Globalization;
using System.Text;
using BifrostQL.Core.Model;

namespace BifrostQL.Core.Storage;

/// <summary>
/// The address an object key resolves to: the file-bearing column plus the
/// positional primary key of the row that owns it, in the table's declared key
/// order (composite-key safe — never a first-column guess).
/// </summary>
public sealed record FileObjectAddress(ColumnDto Column, IReadOnlyList<object?> PrimaryKey);

/// <summary>
/// The deterministic, injective mapping between S3 bucket/object-key coordinates
/// and Bifrost (table, file column, row) coordinates.
///
/// <para><b>Bucket = table.</b> The bucket name is the table's database name
/// lowercased, because S3 bucket names are lowercase. Lowercasing is the ONLY
/// normalization applied, and its collisions are <i>detected, not collapsed</i>:
/// two tables differing only by case make that bucket ambiguous and
/// <see cref="ResolveBucket"/> throws rather than silently exposing one of them.
/// A table whose lowercased name is not a legal bucket name is honestly rejected
/// (it is simply not addressable over S3) rather than rewritten into some other
/// name that could collide with a different table.</para>
///
/// <para><b>Key = <c>{column}/{pk0}/{pk1}/…</c></b>, each component escaped with
/// <see cref="Escape"/>. Injectivity: <see cref="Escape"/> is a standard
/// percent-encoding over the unreserved set <c>[A-Za-z0-9_-]</c>, so it is
/// injective and its output can never contain <c>/</c>. A separator that cannot
/// occur inside any component makes the <c>/</c>-join injective on the tuple;
/// since the column name and the key arity are fixed per table,
/// <c>(column, key-tuple) → key</c> is injective. Two distinct rows therefore
/// cannot collapse onto one object key.</para>
///
/// <para><b>Traversal is impossible by construction</b>, not by filtering: <c>.</c>
/// is outside the unreserved set, so it escapes to <c>%2E</c> and no component can
/// ever emerge as a <c>.</c> or <c>..</c> segment; no component can be empty (an
/// empty segment is what <c>Path.Combine</c> silently swallows, collapsing
/// <c>("", "b")</c> onto <c>("b")</c>); and a key never begins with <c>/</c>.</para>
///
/// <para>This deliberately does NOT reuse <see cref="FileMetadata.GenerateFileKey"/>,
/// which is unusable for an S3 key: it is non-deterministic (timestamp + random
/// suffix), so a key cannot be mapped back to its row, and its sanitizer replaces
/// every invalid character with <c>_</c>, collapsing the distinct ids <c>a/b</c>
/// and <c>a_b</c>.</para>
/// </summary>
public static class S3ObjectKeyMap
{
    private const int MinBucketNameLength = 3;
    private const int MaxBucketNameLength = 63;

    /// <summary>
    /// The bucket name addressing <paramref name="table"/>. Throws when the
    /// table's name is not a legal S3 bucket name once lowercased.
    /// </summary>
    public static string BucketNameFor(IDbTable table)
    {
        ArgumentNullException.ThrowIfNull(table);

        var bucket = table.DbName.ToLowerInvariant();
        if (!IsLegalBucketName(bucket))
            throw new InvalidOperationException(
                $"Table '{table.DbName}' is not addressable as an S3 bucket: '{bucket}' is not a legal bucket name " +
                "(3-63 characters of lowercase letters, digits, '.' or '-', starting and ending alphanumeric, " +
                "and not IP-address shaped).");

        return bucket;
    }

    /// <summary>
    /// Resolves the table a bucket addresses. Unknown buckets and buckets made
    /// ambiguous by case-only table-name collisions both throw — there is no
    /// fallback to "the first match".
    /// </summary>
    public static IDbTable ResolveBucket(IDbModel model, string bucket)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (string.IsNullOrWhiteSpace(bucket) || !IsLegalBucketName(bucket))
            throw new InvalidOperationException($"'{bucket}' is not a legal S3 bucket name.");

        var matches = model.Tables
            .Where(t => string.Equals(t.DbName.ToLowerInvariant(), bucket, StringComparison.Ordinal))
            .ToList();

        if (matches.Count > 1)
            throw new InvalidOperationException(
                $"Bucket '{bucket}' is ambiguous: tables {string.Join(", ", matches.Select(t => $"'{t.DbName}'"))} " +
                "all map onto it. Case-only table-name collisions must be resolved before these tables can be " +
                "served over S3.");

        return matches.Count == 1
            ? matches[0]
            : throw new InvalidOperationException($"Bucket '{bucket}' does not exist.");
    }

    /// <summary>
    /// Builds the object key addressing <paramref name="column"/> of the row with
    /// the given positional <paramref name="primaryKey"/> (in the table's declared
    /// key order).
    /// </summary>
    public static string KeyFor(ColumnDto column, IReadOnlyList<object?> primaryKey)
    {
        ArgumentNullException.ThrowIfNull(column);
        ArgumentNullException.ThrowIfNull(primaryKey);

        if (primaryKey.Count == 0)
            throw new InvalidOperationException("An object key requires at least one primary-key component.");

        var parts = new List<string>(primaryKey.Count + 1) { Escape(column.ColumnName) };
        foreach (var value in primaryKey)
            parts.Add(Escape(Stringify(value)));

        return string.Join('/', parts);
    }

    /// <summary>
    /// Parses an object key back into the column and typed primary-key values it
    /// addresses. Every failure mode — unknown column, wrong key arity, a
    /// component that is not the key column's type — throws; a key is never
    /// partially matched.
    /// </summary>
    public static FileObjectAddress ParseKey(IDbTable table, string key)
    {
        ArgumentNullException.ThrowIfNull(table);

        if (string.IsNullOrEmpty(key))
            throw new InvalidOperationException("Object key is required.");

        var keyColumns = table.KeyColumns.ToList();
        if (keyColumns.Count == 0)
            throw new InvalidOperationException(
                $"Table '{table.DbName}' has no primary key, so its rows cannot be addressed as objects.");

        var parts = key.Split('/');
        if (parts.Length != keyColumns.Count + 1)
            throw new InvalidOperationException(
                $"Object key '{key}' does not address a row of '{table.DbName}': expected " +
                $"'{{column}}/{{{string.Join("}}/{{", keyColumns.Select(c => c.ColumnName))}}}' " +
                $"({keyColumns.Count + 1} components), got {parts.Length}.");

        var columnName = Unescape(parts[0]);
        if (!table.ColumnLookup.TryGetValue(columnName, out var column))
            throw new InvalidOperationException($"Column '{columnName}' does not exist in table '{table.DbName}'.");

        var primaryKey = new object?[keyColumns.Count];
        for (var i = 0; i < keyColumns.Count; i++)
            primaryKey[i] = Convert(keyColumns[i], Unescape(parts[i + 1]));

        return new FileObjectAddress(column, primaryKey);
    }

    /// <summary>
    /// Percent-encodes over the unreserved set <c>[A-Za-z0-9_-]</c>. Injective,
    /// never emits <c>/</c>, and never emits a bare <c>.</c>, so no output can be
    /// read as a path separator or a traversal segment.
    /// </summary>
    private static string Escape(string value)
    {
        if (string.IsNullOrEmpty(value))
            throw new InvalidOperationException(
                "An empty object-key component is not addressable: an empty path segment is silently dropped " +
                "when combined into a storage path, which would collapse two distinct rows onto one key.");

        var builder = new StringBuilder(value.Length);
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            if (b is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z' or >= (byte)'0' and <= (byte)'9'
                or (byte)'_' or (byte)'-')
                builder.Append((char)b);
            else
                builder.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    private static string Unescape(string value)
    {
        if (string.IsNullOrEmpty(value))
            throw new InvalidOperationException("Object key contains an empty component.");

        var bytes = new List<byte>(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '%')
            {
                // Reject anything Escape would never have emitted, so a key has
                // exactly one encoding and cannot be smuggled in unescaped.
                if (value[i] is not (>= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-'))
                    throw new InvalidOperationException($"Object key component '{value}' is not correctly encoded.");
                bytes.Add((byte)value[i]);
                continue;
            }

            if (i + 2 >= value.Length
                || !byte.TryParse(value.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var decoded))
                throw new InvalidOperationException($"Object key component '{value}' has a malformed escape sequence.");

            bytes.Add(decoded);
            i += 2;
        }

        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    /// <summary>
    /// Renders a key value as text injectively and culture-invariantly. BigInt
    /// values render as decimal strings — never floats — so no precision is lost.
    /// </summary>
    private static string Stringify(object? value) => value switch
    {
        null => throw new InvalidOperationException(
            "A null primary-key component cannot address a row."),
        string s => s,
        DateTime d => d.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset d => d.ToString("O", CultureInfo.InvariantCulture),
        Guid g => g.ToString("D"),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? throw new InvalidOperationException(
            $"Primary-key component of type '{value.GetType().Name}' has no text form."),
    };

    /// <summary>
    /// Converts a decoded key component to the key column's CLR type. A component
    /// that is not the column's shape is rejected — never coerced to a default,
    /// which would silently address a different row.
    /// </summary>
    private static object Convert(ColumnDto keyColumn, string text)
    {
        var normalized = Utils.StringNormalizer.NormalizeType(keyColumn.DataType);
        try
        {
            return normalized switch
            {
                "int" or "integer" => int.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture),
                "bigint" => long.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture),
                "smallint" => short.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture),
                "tinyint" => byte.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture),
                "uniqueidentifier" or "uuid" => Guid.Parse(text),
                _ => text,
            };
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        {
            // Per .claude/rules/protocol-adapter-security.md invariant 5: a decode
            // of untrusted wire input built on a .Parse-family call must catch the
            // full parse-exception family, not just the obviously-malformed case.
            // An out-of-range component (OverflowException) is as attacker-reachable
            // as a non-numeric one.
            throw new InvalidOperationException(
                $"Object key component '{text}' is not a valid value for key column " +
                $"'{keyColumn.ColumnName}' ({keyColumn.DataType}).", ex);
        }
    }

    private static bool IsLegalBucketName(string bucket)
    {
        if (bucket.Length is < MinBucketNameLength or > MaxBucketNameLength)
            return false;

        if (!IsBucketAlphanumeric(bucket[0]) || !IsBucketAlphanumeric(bucket[^1]))
            return false;

        foreach (var c in bucket)
        {
            if (!IsBucketAlphanumeric(c) && c is not ('-' or '.'))
                return false;
        }

        // An IP-address-shaped name is reserved by S3 and would be ambiguous with
        // path-style addressing against a host.
        return !System.Net.IPAddress.TryParse(bucket, out _);
    }

    private static bool IsBucketAlphanumeric(char c) => c is >= 'a' and <= 'z' or >= '0' and <= '9';
}
