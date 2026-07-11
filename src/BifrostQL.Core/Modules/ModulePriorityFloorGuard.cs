using System;
using System.Collections.Generic;
using System.Linq;

namespace BifrostQL.Core.Modules;

/// <summary>
/// Composition-time guard that keeps the reserved security band (priorities 0-99, below
/// <see cref="BifrostProfile.SecurityBandFloor"/>) for the host's built-in transformers.
///
/// Transformer priority bands are otherwise advisory: nothing stopped a consumer-supplied
/// transformer from declaring priority 0 and thereby running <em>ahead</em> of tenant
/// isolation, authorization policy, or the state machine — a silent ordering hazard where a
/// consumer sees (or rewrites) a mutation before the security guards gate it. This guard
/// rejects such a transformer when the transformer set is composed, so the misconfiguration
/// fails fast at startup rather than silently reordering security enforcement.
///
/// Exemptions:
/// <list type="bullet">
/// <item>Built-in transformers shipped in the BifrostQL.Core assembly (the host's own
/// security/data-integrity modules) are trusted and never checked.</item>
/// <item>A consumer transformer that implements <see cref="IAllowSecurityBandPriority"/>
/// has explicitly acknowledged running in the security band.</item>
/// </list>
/// </summary>
public static class ModulePriorityFloorGuard
{
    // Built-ins live in the same assembly as this guard (BifrostQL.Core). A transformer
    // whose concrete type is defined here is a trusted host transformer — including a
    // caller-customized instance of a built-in type (e.g. a PolicyMutationTransformer
    // constructed with a non-default admin role) — and is exempt from the floor.
    private static readonly System.Reflection.Assembly HostAssembly = typeof(ModulePriorityFloorGuard).Assembly;

    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> if any consumer transformer in
    /// <paramref name="transformers"/> declares a priority below
    /// <see cref="BifrostProfile.SecurityBandFloor"/> without opting in via
    /// <see cref="IAllowSecurityBandPriority"/>. <paramref name="priorityOf"/> reads the
    /// priority off each element (filter and mutation transformers expose it separately,
    /// with no shared base type). <paramref name="kind"/> names the collection in the error
    /// (e.g. "filter", "mutation").
    /// </summary>
    public static void EnsureConsumerPrioritiesRespectSecurityFloor<T>(
        IEnumerable<T> transformers, Func<T, int> priorityOf, string kind)
        where T : notnull
    {
        var violations = new List<string>();

        foreach (var transformer in transformers)
        {
            var priority = priorityOf(transformer);
            if (priority >= BifrostProfile.SecurityBandFloor)
                continue; // Data-integrity/application band — never reserved.

            var type = transformer.GetType();
            if (type.Assembly == HostAssembly)
                continue; // Trusted built-in (or a customized built-in instance).

            if (transformer is IAllowSecurityBandPriority)
                continue; // Explicit opt-in.

            violations.Add(
                $"  {type.FullName} (priority {priority}): consumer {kind} transformers must use " +
                $"priority {BifrostProfile.SecurityBandFloor} or higher; priorities 0-{BifrostProfile.SecurityBandFloor - 1} " +
                "are reserved for the host's built-in security transformers, which a lower-priority " +
                $"consumer transformer would silently run ahead of. Raise its priority to at least " +
                $"{BifrostProfile.SecurityBandFloor}, or implement {nameof(IAllowSecurityBandPriority)} to opt in deliberately.");
        }

        if (violations.Count > 0)
        {
            throw new InvalidOperationException(
                $"Consumer {kind} transformer(s) declared a priority inside the reserved security band:" +
                Environment.NewLine + string.Join(Environment.NewLine, violations));
        }
    }
}
