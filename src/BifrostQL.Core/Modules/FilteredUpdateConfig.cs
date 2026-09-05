using BifrostQL.Core.Model;

namespace BifrostQL.Core.Modules;

/// <summary>
/// The single reading of a table's filtered set-update opt-in
/// (<see cref="MetadataKeys.FilteredUpdate"/>): the schema generator consults it to decide
/// whether the <c>updateWhere</c> argument and its input types exist at all, and the
/// pipeline re-checks it fail-closed before executing — the SDL gate alone is not a
/// security boundary (a stale schema or a direct executor caller must still be refused).
/// </summary>
public static class FilteredUpdateConfig
{
    public const string EnabledValue = "enabled";
    public const int DefaultMaxAffected = 100;

    public static bool IsEnabled(IDbTable table) =>
        string.Equals(table.GetMetadataValue(MetadataKeys.FilteredUpdate.Enabled), EnabledValue, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Max rows one filtered update may affect (default 100, aligned with batch-max-size).
    /// ModelConfigValidator rejects a malformed value at model load; this backstop covers
    /// models built without validation, surfacing a wire-safe BifrostExecutionError
    /// instead of a raw InvalidOperationException.
    /// </summary>
    public static int MaxAffected(IDbTable table)
    {
        try
        {
            return Utils.MetadataNumber.PositiveInt(
                table.GetMetadataValue(MetadataKeys.FilteredUpdate.MaxAffected), DefaultMaxAffected, MetadataKeys.FilteredUpdate.MaxAffected);
        }
        catch (InvalidOperationException ex)
        {
            throw new Resolvers.BifrostExecutionError(ex.Message, ex);
        }
    }
}
