using System;
using System.Globalization;
using BifrostQL.Core.Model;

namespace BifrostQL.Core.Modules.Deferred;

/// <summary>
/// Parsed per-table reversible-deferred-effects configuration. A table without
/// <c>deferrable</c> returns <see cref="None"/> even if it carries partial deferred
/// metadata: only the explicit marker may opt a table into the undo contract.
/// </summary>
public sealed class DeferredConfig
{
    /// <summary>The no-deferred-effects sentinel for tables that have not opted in.</summary>
    public static readonly DeferredConfig None = new(undoWindow: null, holdEvents: false);

    private DeferredConfig(TimeSpan? undoWindow, bool holdEvents)
    {
        UndoWindow = undoWindow;
        HoldEvents = holdEvents;
    }

    /// <summary>Whether the table explicitly permits reversible deferred effects.</summary>
    public bool IsDeferrable => UndoWindow.HasValue;

    /// <summary>The explicitly configured interval during which a change can be reversed.</summary>
    public TimeSpan? UndoWindow { get; }

    /// <summary>Whether outbound events remain held until the change becomes final.</summary>
    public bool HoldEvents { get; }

    /// <summary>
    /// Parses one table's deferred-effects metadata. A marked table requires an explicit,
    /// positive <c>undo-window</c>; there is deliberately no default window.
    /// </summary>
    public static DeferredConfig FromTable(IDbTable table)
    {
        ArgumentNullException.ThrowIfNull(table);

        var marker = table.GetMetadataValue(MetadataKeys.Deferred.Deferrable);
        if (string.IsNullOrWhiteSpace(marker))
            return None;

        if (!string.Equals(marker.Trim(), MetadataKeys.Deferred.Enabled, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"'{MetadataKeys.Deferred.Deferrable}' on '{table.TableSchema}.{table.DbName}' must be '{MetadataKeys.Deferred.Enabled}'.");

        var undoWindow = ParseDuration(table, table.GetMetadataValue(MetadataKeys.Deferred.UndoWindow));
        var holdEvents = ParseHoldEvents(table.GetMetadataValue(MetadataKeys.Deferred.HoldEvents), table);
        return new DeferredConfig(undoWindow, holdEvents);
    }

    private static TimeSpan ParseDuration(IDbTable table, string? raw)
    {
        var token = raw?.Trim();
        if (string.IsNullOrEmpty(token) || token.Length < 2)
            throw InvalidDuration(table, raw ?? string.Empty, "a duration is required");

        var unit = char.ToLowerInvariant(token[^1]);
        if (!int.TryParse(token[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value <= 0)
            throw InvalidDuration(table, token, "the magnitude must be a positive integer");

        try
        {
            return unit switch
            {
                'd' => TimeSpan.FromDays(value),
                'h' => TimeSpan.FromHours(value),
                _ => throw InvalidDuration(table, token, $"'{token[^1]}' is not a recognized unit"),
            };
        }
        catch (Exception exception) when (exception is OverflowException or ArgumentOutOfRangeException)
        {
            throw InvalidDuration(table, token, "the window is too large to represent");
        }
    }

    private static bool ParseHoldEvents(string? raw, IDbTable table)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        if (string.Equals(raw.Trim(), MetadataKeys.Deferred.Enabled, StringComparison.OrdinalIgnoreCase))
            return true;

        throw new InvalidOperationException(
            $"'{MetadataKeys.Deferred.HoldEvents}' on '{table.TableSchema}.{table.DbName}' must be '{MetadataKeys.Deferred.Enabled}' when present.");
    }

    private static InvalidOperationException InvalidDuration(IDbTable table, string token, string detail) =>
        new(
            $"'{MetadataKeys.Deferred.UndoWindow}' on '{table.TableSchema}.{table.DbName}' has an invalid duration '{token}': {detail}. " +
            "Use a positive integer followed by 'd' (days) or 'h' (hours), e.g. '90d' or '12h'.");
}
