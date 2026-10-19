namespace BifrostQL.Core.Resolvers;

/// <summary>
/// The capability that lets a <see cref="QueryIntent"/> or <see cref="MutationIntent"/> name one
/// of the endpoint's <c>visibility: hidden</c> tables (<see cref="Model.IDbModel.HiddenTables"/>).
///
/// <para><b>Why a capability, not a bool.</b> A hidden table is withheld from every exposed
/// surface, and every protocol adapter, MCP tool and host caller reaches the database through the
/// same intent executors. Expressed as a public bool, any of them could reach a hidden table by
/// setting it. This type has no public constructor and no public instance, so a value can only
/// come from BifrostQL.Core's internals (the retention purge) and the first-party assemblies on
/// Core's <c>InternalsVisibleTo</c> list; for everyone else a hidden table resolves exactly like a
/// table that does not exist. Same shape as <see cref="MutationRestoreCapability"/>.</para>
/// </summary>
public sealed class HiddenTableAccess
{
    internal static readonly HiddenTableAccess Granted = new();

    private HiddenTableAccess() { }

    /// <summary>
    /// Resolves an intent's table name: over the exposed tables only, or over exposed and hidden
    /// tables together when the intent carries the capability.
    /// </summary>
    internal static bool TryResolve(
        Model.IDbModel model, string clientName, HiddenTableAccess? access,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Model.IDbTable? table)
        => access is null
            ? model.TryGetTableFromClientName(clientName, out table)
            : model.TryGetTableIncludingHidden(clientName, out table);
}
