using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Resolvers;

namespace BifrostQL.Core.Auth;

/// <summary>
/// Collects every user-context key that a SECURITY filter resolves at request time for a
/// given model: the policy row-scope placeholders (<c>policy-row-scope: col = {"{key}"}</c>)
/// and the auto-filter claim keys (<c>auto-filter: col:claim</c>).
///
/// <para>The point is the wire-context seam (<see cref="WireContextMerger"/>): a key a
/// security filter reads is part of the IDENTITY, not the request. If a wire-supplied entry
/// could fill one of these keys, a client whose identity omits the claim (e.g. no
/// <c>household_id</c>) could supply the value itself and steer the row-scope predicate —
/// the filter would run against attacker-chosen scope instead of fail-closed access denial.
/// The merger therefore refuses wire entries for these keys exactly as it refuses the six
/// canonical identity keys.</para>
/// </summary>
internal static class SecurityContextKeyCollector
{
    public static IReadOnlySet<string> Collect(IDbModel model)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in model.Tables)
        {
            var policy = PolicyConfigCollector.FromTable(table);
            if (policy.RowScopeExpression is { } expression &&
                RowScopeCompiler.TryGetContextKey(expression, out var scopeKey))
            {
                keys.Add(scopeKey);
            }

            if (table.Metadata.TryGetValue(MetadataKeys.Security.AutoFilter, out var mappingValue) &&
                mappingValue is string mappingStr && !string.IsNullOrWhiteSpace(mappingStr))
            {
                // A malformed mapping is skipped here; the request-time transformer parses the
                // same string and fails closed on it, so skipping cannot open a hole — there
                // is no claim key to protect in a mapping that cannot parse.
                try
                {
                    foreach (var mapping in AutoFilterTransformer.ParseMappings(
                                 mappingStr, $"{table.TableSchema}.{table.DbName}"))
                    {
                        keys.Add(mapping.Claim);
                    }
                }
                catch (BifrostExecutionError)
                {
                    // Fail closed at request time; nothing to protect here.
                }
            }
        }

        return keys;
    }
}
