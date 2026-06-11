using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;

namespace BifrostQL.Core.Modules;

/// <summary>
/// A GraphQL argument contributed by a module to a table's query or mutation
/// field. <see cref="Name"/> is the client-facing argument name (underscore
/// prefixed by convention, e.g. <c>_includeDeleted</c>); <see cref="ContextKey"/>
/// is the server-side key the module's transformers read the value back from.
/// </summary>
public sealed record ModuleArgument(
    string Name,
    string GraphQlType,
    string ContextKey,
    string Description);

/// <summary>
/// The API surface a module exposes on the GraphQL schema. Modules that act
/// on tables tagged with their metadata declare per-table arguments here;
/// schema generation emits them and the resolvers capture their values back
/// into the transform pipeline. Modules with no client-facing surface
/// (tenant isolation, policy, audit) simply return no arguments.
/// </summary>
public interface IModuleApi
{
    string ModuleName { get; }

    /// <summary>Arguments added to the table's query field when the module is active on the table.</summary>
    IEnumerable<ModuleArgument> GetQueryArguments(IDbTable table);

    /// <summary>Arguments added to the table's mutation field when the module is active on the table.</summary>
    IEnumerable<ModuleArgument> GetMutationArguments(IDbTable table);
}

/// <summary>
/// Soft delete's client-facing API:
///   query  — <c>_includeDeleted: Boolean</c> (deleted rows included),
///            <c>_onlyDeleted: Boolean</c> (only deleted rows; wins over _includeDeleted)
///   delete — <c>_hardDelete: Boolean</c> (real DELETE, bypasses the soft-delete rewrite;
///            optionally gated by the <c>soft-delete-hard-role</c> table metadata key)
/// </summary>
public sealed class SoftDeleteModuleApi : IModuleApi
{
    public const string IncludeDeletedArg = "_includeDeleted";
    public const string OnlyDeletedArg = "_onlyDeleted";
    public const string HardDeleteArg = "_hardDelete";

    public const string IncludeDeletedKey = "include_deleted";
    public const string OnlyDeletedKey = "only_deleted";
    public const string HardDeleteKey = "hard_delete";

    public string ModuleName => MetadataKeys.SoftDelete.Column;

    private static bool IsEnabled(IDbTable table) =>
        table.Metadata.TryGetValue(MetadataKeys.SoftDelete.Column, out var val) && val != null;

    public IEnumerable<ModuleArgument> GetQueryArguments(IDbTable table)
    {
        if (!IsEnabled(table))
            yield break;
        yield return new ModuleArgument(IncludeDeletedArg, "Boolean", IncludeDeletedKey,
            "Include soft-deleted rows in the result.");
        yield return new ModuleArgument(OnlyDeletedArg, "Boolean", OnlyDeletedKey,
            "Return only soft-deleted rows. Takes precedence over _includeDeleted.");
    }

    public IEnumerable<ModuleArgument> GetMutationArguments(IDbTable table)
    {
        if (!IsEnabled(table))
            yield break;
        yield return new ModuleArgument(HardDeleteArg, "Boolean", HardDeleteKey,
            "Permanently delete the row instead of soft-deleting it.");
    }
}

/// <summary>
/// Registry of module API surfaces. Schema generators ask it for the SDL
/// argument fragments per table; resolvers ask it to capture supplied
/// argument values back out of a request. Adding a module's client-facing
/// controls means adding one <see cref="IModuleApi"/> entry here — schema
/// emission and value capture then follow automatically.
/// </summary>
public static class ModuleApiRegistry
{
    public static readonly IReadOnlyList<IModuleApi> BuiltIns = new IModuleApi[]
    {
        new SoftDeleteModuleApi(),
    };

    /// <summary>SDL fragment (leading-space separated) for the table's query field, e.g. " _includeDeleted: Boolean".</summary>
    public static string QueryArgumentsSdl(IDbTable table) =>
        string.Concat(BuiltIns
            .SelectMany(m => m.GetQueryArguments(table))
            .Select(a => $" {a.Name}: {a.GraphQlType}"));

    /// <summary>SDL fragment (comma prefixed) for the table's mutation field, e.g. ", _hardDelete: Boolean".</summary>
    public static string MutationArgumentsSdl(IDbTable table) =>
        string.Concat(BuiltIns
            .SelectMany(m => m.GetMutationArguments(table))
            .Select(a => $", {a.Name}: {a.GraphQlType}"));

    /// <summary>
    /// Copies module query-argument values supplied on the request into
    /// <paramref name="userContext"/> under table-scoped keys
    /// (<c>{contextKey}:{schema}.{table}</c>) so they affect only the table
    /// the argument was written on. Transformers also honor the bare global
    /// key for server-side overrides set by host code.
    /// </summary>
    public static void CaptureQueryArguments(IBifrostFieldContext context, IDbTable table, IDictionary<string, object?> userContext)
    {
        foreach (var arg in BuiltIns.SelectMany(m => m.GetQueryArguments(table)))
        {
            if (!context.HasArgument(arg.Name))
                continue;
            userContext[ScopedKey(arg.ContextKey, table)] = ReadArgument(context, arg);
        }
    }

    /// <summary>
    /// Reads module mutation-argument values supplied on the request into a
    /// dictionary keyed by context key, for <see cref="MutationTransformContext.ModuleArguments"/>.
    /// </summary>
    public static IReadOnlyDictionary<string, object?> CaptureMutationArguments(IBifrostFieldContext context, IDbTable table)
    {
        Dictionary<string, object?>? captured = null;
        foreach (var arg in BuiltIns.SelectMany(m => m.GetMutationArguments(table)))
        {
            if (!context.HasArgument(arg.Name))
                continue;
            captured ??= new Dictionary<string, object?>();
            captured[arg.ContextKey] = ReadArgument(context, arg);
        }
        return captured ?? EmptyArguments;
    }

    public static readonly IReadOnlyDictionary<string, object?> EmptyArguments =
        new Dictionary<string, object?>();

    public static string ScopedKey(string contextKey, IDbTable table) =>
        $"{contextKey}:{table.TableSchema}.{table.DbName}";

    /// <summary>
    /// True when the flag is set for this table — either via the table-scoped
    /// key written by <see cref="CaptureQueryArguments"/> or via the bare
    /// global key set server-side in the user context.
    /// </summary>
    public static bool GetFlag(IDictionary<string, object?> userContext, string contextKey, IDbTable table)
    {
        if (userContext.TryGetValue(ScopedKey(contextKey, table), out var scoped) && scoped is true)
            return true;
        return userContext.TryGetValue(contextKey, out var global) && global is true;
    }

    private static object? ReadArgument(IBifrostFieldContext context, ModuleArgument arg) =>
        arg.GraphQlType.StartsWith("Boolean", StringComparison.Ordinal) ? context.GetArgument<bool>(arg.Name)
        : arg.GraphQlType.StartsWith("Int", StringComparison.Ordinal) ? context.GetArgument<int>(arg.Name)
        : context.GetArgument<string>(arg.Name);
}
