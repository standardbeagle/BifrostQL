namespace BifrostQL.Mcp
{
    /// <summary>
    /// Guardrail thresholds on the size of the declared MCP tool surface. Both thresholds are
    /// options (never constants baked into the loader) so a deployment can tune them to its
    /// tolerance. The intent is to keep the tool surface small: a large, undifferentiated tool
    /// list degrades an agent's ability to pick the right tool, so the loader nudges toward
    /// consolidation (warn) and refuses an unbounded surface outright (hard cap).
    /// </summary>
    public sealed class McpToolBudgetOptions
    {
        /// <summary>
        /// The declared-tool count above which the loader logs a consolidation warning. Default 12.
        /// A warning — not a failure: the surface still loads.
        /// </summary>
        public int WarnThreshold { get; set; } = 12;

        /// <summary>
        /// The declared-tool count above which the loader FAILS to load (throws). Configurable;
        /// default 24. Must sit at or above <see cref="WarnThreshold"/> for the warn band to exist.
        /// </summary>
        public int HardCap { get; set; } = 24;
    }

    /// <summary>
    /// Governs the MCP tool surface: the <see cref="Budget"/> guardrail on how many tools may be
    /// declared, and the optional per-identity <see cref="RoleToolAllowList"/> that hides role-gated
    /// tools from callers who lack the role. Both are opt-in refinements — the default instance
    /// imposes the standard budget and gates nothing, so the full surface is visible.
    /// </summary>
    public sealed class McpToolPolicyOptions
    {
        /// <summary>The declared-tool-count guardrail. Never null; defaults to the standard budget.</summary>
        public McpToolBudgetOptions Budget { get; set; } = new();

        /// <summary>
        /// Optional per-identity tool filtering, keyed by role name → the tool names that role is
        /// allowed to see and call. A tool that appears in AT LEAST ONE role's list becomes
        /// <i>role-gated</i>: only identities holding one of the roles whose list names it may see or
        /// invoke it. A tool named by NO list stays ungated and visible to every authenticated
        /// identity (the guardrail never silently hides the default surface). Filtering is fail-closed:
        /// an identity with no matching role sees a list excluding every role-gated tool, and a direct
        /// call to a hidden tool is refused. Empty (the default) gates nothing.
        /// </summary>
        public IReadOnlyDictionary<string, IReadOnlyList<string>> RoleToolAllowList { get; set; }
            = new Dictionary<string, IReadOnlyList<string>>();
    }
}
