using System.Text.RegularExpressions;
using BifrostQL.Core.Utils;

namespace BifrostQL.Core.Model;

/// <summary>
/// The table metadata consumed by the feed surface. A table is deliberately not a
/// feed until it names <see cref="TimestampColumn"/>; this keeps publication opt-in.
/// </summary>
public sealed class FeedConfig
{
    private static readonly Regex Placeholder = new(@"\{([A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.Compiled);

    private FeedConfig(string? timestampColumn, string? titleTemplate, string? bodyColumn, string? linkTemplate,
        IReadOnlyList<string> primaryKeyColumns)
    {
        TimestampColumn = timestampColumn;
        TitleTemplate = titleTemplate;
        BodyColumn = bodyColumn;
        LinkTemplate = linkTemplate;
        PrimaryKeyColumns = primaryKeyColumns;
    }

    public bool IsEnabled => TimestampColumn is not null;
    public string? TimestampColumn { get; }
    public string? TitleTemplate { get; }
    public string? BodyColumn { get; }
    public string? LinkTemplate { get; }
    public IReadOnlyList<string> PrimaryKeyColumns { get; }

    public static FeedConfig FromTable(IDbTable table)
    {
        ArgumentNullException.ThrowIfNull(table);

        return new FeedConfig(
            NormalizeColumnName(table.GetMetadataValue(MetadataKeys.Feed.Timestamp)),
            table.GetMetadataValue(MetadataKeys.Feed.Title)?.Trim(),
            NormalizeColumnName(table.GetMetadataValue(MetadataKeys.Feed.Body)),
            table.GetMetadataValue(MetadataKeys.Feed.Link)?.Trim(),
            table.KeyColumns.Select(column => column.ColumnName).ToArray());
    }

    internal static IReadOnlyList<string> GetPlaceholders(string template)
    {
        var matches = Placeholder.Matches(template);
        var reconstructed = Placeholder.Replace(template, string.Empty);
        if (reconstructed.Contains('{') || reconstructed.Contains('}'))
            return Array.Empty<string>();

        return matches.Select(match => match.Groups[1].Value).ToArray();
    }

    internal static bool HasMalformedPlaceholders(string template)
    {
        var withoutPlaceholders = Placeholder.Replace(template, string.Empty);
        return withoutPlaceholders.Contains('{') || withoutPlaceholders.Contains('}');
    }

    private static string? NormalizeColumnName(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : StringNormalizer.NormalizeName(value);
}
