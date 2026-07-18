using ModelContextProtocol.Protocol;

namespace BifrostQL.Mcp
{
    /// <summary>
    /// Enforces the per-identity tool allow-list from <see cref="McpToolPolicyOptions.RoleToolAllowList"/>.
    /// Built once from the policy at load time (the role→tool map is inverted into a tool→roles map so a
    /// lookup is O(1) per tool), then consulted on every <c>tools/list</c> and every <c>tools/call</c> so
    /// the two agree by construction — a tool hidden from the list can never be invoked by name.
    ///
    /// <para><b>Fail-closed.</b> A role-gated tool is visible only to a caller holding one of its permitted
    /// roles; an identity with no matching role (including one whose roles could not be resolved) sees the
    /// tool neither in the list nor when it calls it directly. A tool named by no allow-list is ungated and
    /// stays visible to every authenticated identity, so opting one tool into gating never silently hides
    /// the rest of the surface.</para>
    /// </summary>
    internal sealed class McpToolAccessGate
    {
        /// <summary>Gate that gates nothing — the whole surface is visible. Used when no allow-list is configured.</summary>
        internal static readonly McpToolAccessGate Open = new(new Dictionary<string, HashSet<string>>(StringComparer.Ordinal));

        // tool name -> the set of roles permitted to see/call it. A tool absent from this map is ungated.
        private readonly IReadOnlyDictionary<string, HashSet<string>> _toolRoles;

        private McpToolAccessGate(IReadOnlyDictionary<string, HashSet<string>> toolRoles) => _toolRoles = toolRoles;

        /// <summary>True when at least one tool is role-gated; when false the gate is a no-op.</summary>
        internal bool GatesAnything => _toolRoles.Count > 0;

        /// <summary>
        /// Builds the gate from <paramref name="policy"/>. The role→tools allow-list is inverted into a
        /// tool→roles map; a null/empty allow-list yields <see cref="Open"/>. Role names are matched
        /// case-insensitively (an identity's projected roles need not match the configured casing); tool
        /// names are matched exactly, as they arrive on the wire.
        /// </summary>
        internal static McpToolAccessGate From(McpToolPolicyOptions? policy)
        {
            var allowList = policy?.RoleToolAllowList;
            if (allowList is null || allowList.Count == 0)
                return Open;

            var toolRoles = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var (role, tools) in allowList)
            {
                if (string.IsNullOrWhiteSpace(role) || tools is null)
                    continue;
                foreach (var tool in tools)
                {
                    if (string.IsNullOrWhiteSpace(tool))
                        continue;
                    if (!toolRoles.TryGetValue(tool, out var roles))
                        toolRoles[tool] = roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    roles.Add(role);
                }
            }

            return toolRoles.Count == 0 ? Open : new McpToolAccessGate(toolRoles);
        }

        /// <summary>True when <paramref name="toolName"/> carries a role allow-list (is role-gated).</summary>
        internal bool IsGated(string toolName) => _toolRoles.ContainsKey(toolName);

        /// <summary>
        /// Whether <paramref name="toolName"/> is visible to a caller holding <paramref name="roles"/>.
        /// An ungated tool is always visible; a gated tool is visible only when the caller holds one of its
        /// permitted roles — fail-closed for the empty-roles case.
        /// </summary>
        internal bool IsVisible(string toolName, IReadOnlyCollection<string> roles)
        {
            if (!_toolRoles.TryGetValue(toolName, out var allowedRoles))
                return true;
            return roles.Any(allowedRoles.Contains);
        }

        /// <summary>Filters <paramref name="tools"/> to those visible to a caller holding <paramref name="roles"/>.</summary>
        internal List<Tool> Filter(IEnumerable<Tool> tools, IReadOnlyCollection<string> roles)
            => tools.Where(t => IsVisible(t.Name, roles)).ToList();
    }
}
