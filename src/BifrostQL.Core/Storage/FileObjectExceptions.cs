using BifrostQL.Core.Resolvers;

namespace BifrostQL.Core.Storage;

/// <summary>
/// A post-authorization failure in the <see cref="FileObjectSeam"/> that LEFT
/// storage residue an operator must reclaim: an orphaned blob whose storage key is
/// carried on <see cref="StorageKey"/>. Two seam sites raise it — a compensating
/// rollback that itself failed after the pointer write failed (PUT double-fault),
/// and a blob delete that failed after the row pointer was already cleared
/// (DELETE post-clear).
///
/// <para><b>Why a distinct type.</b> The seam otherwise surfaces denial, a corrupt
/// pointer, and a scoped-away write all as a plain <see cref="BifrostExecutionError"/>,
/// which the wire deliberately folds into the non-enumerating <c>NoSuchKey</c> — a
/// wire mapper cannot tell those apart, nor should it, because responding
/// differently to them WOULD weaken non-enumeration. Residue is the one case that
/// both needs a different wire response AND leaks nothing by getting one: it only
/// ever occurs on the caller's OWN already-authorized write, so surfacing it
/// (500 on PUT, still-204 on DELETE) plus an operator log is safe. Without this
/// type the residue was swallowed at <c>Debug</c> and the orphaned storage key was
/// lost.</para>
///
/// <para>Derives from <see cref="BifrostExecutionError"/> so a connection handler
/// that already filters its catch on that base still catches it — it does not
/// escape unhandled onto the host (.claude/rules/protocol-adapter-security.md
/// invariant 1). A wire mapper catches this more-derived type FIRST to log the
/// orphan at Error/Warning WITH the storage key. As with any Bifrost-internal
/// exception, the message embeds the storage key deliberately for the operator log
/// and must never be forwarded verbatim onto a client wire (invariant 3).</para>
/// </summary>
public sealed class FileObjectResidueException : BifrostExecutionError
{
    public FileObjectResidueException(string storageKey, string message)
        : base(message) => StorageKey = storageKey;

    public FileObjectResidueException(string storageKey, string message, Exception innerException)
        : base(message, innerException) => StorageKey = storageKey;

    /// <summary>The storage key of the orphaned object an operator must reclaim.</summary>
    public string StorageKey { get; }
}
