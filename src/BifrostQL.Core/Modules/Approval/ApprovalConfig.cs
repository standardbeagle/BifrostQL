using System;
using System.Runtime.CompilerServices;
using BifrostQL.Core.Model;

namespace BifrostQL.Core.Modules.Approval
{
    /// <summary>
    /// Parsed per-table approval-gate configuration. Built from the <c>approval</c> /
    /// <c>approver-role</c> / <c>self-approve</c> table metadata. A table with no
    /// <c>approval</c> key returns <see cref="None"/> — there is NO implicit approval gate
    /// ever, so a table that does not opt in is never gated. Mirrors the
    /// <c>HistoryConfig.FromTable</c> / <c>RetentionConfig.FromTable</c> factory shape: a
    /// per-table metadata read, cached by table identity, that fails fast on a config that
    /// would otherwise silently do the wrong thing.
    ///
    /// The load-bearing fail direction: a half-resolved gate must FAIL CLOSED (not load),
    /// never leave the table ungated. An <c>approval</c> declaration with no
    /// <see cref="ApproverRole"/> is therefore a hard error — a gate nobody can approve is
    /// fail-open. There is no default approver role.
    ///
    /// This slice establishes the metadata contract + fail-fast validation only; the
    /// write-interception transformer that diverts a gated write into a pending_changes row
    /// is a later Approval sub-task.
    /// </summary>
    public sealed class ApprovalConfig
    {
        /// <summary>The no-approval sentinel returned for tables that do not opt in.</summary>
        public static readonly ApprovalConfig None = new(
            requiresApproval: false, approverRole: null, selfApprove: true);

        private ApprovalConfig(bool requiresApproval, string? approverRole, bool selfApprove)
        {
            RequiresApproval = requiresApproval;
            ApproverRole = approverRole;
            SelfApprove = selfApprove;
        }

        /// <summary>Whether the table's writes are gated behind approval (the <c>approval</c> opt-in).</summary>
        public bool RequiresApproval { get; }

        /// <summary>
        /// The role whose holder may approve a pending change on this table. Non-null exactly
        /// when <see cref="RequiresApproval"/> is true — an approval gate without an approver
        /// never loads (fail-closed), so there is no gated config with a null approver.
        /// </summary>
        public string? ApproverRole { get; }

        /// <summary>
        /// Whether a change's requester may approve their own pending change (when they also
        /// hold the <see cref="ApproverRole"/>). Defaults to <c>true</c> unless declared false.
        /// </summary>
        public bool SelfApprove { get; }

        // Cached per table instance for the same reason as HistoryConfig/RetentionConfig:
        // schema generation and ModelConfigValidator both ask for it, and the model is
        // immutable after load. A parse that throws (invalid config) is never cached — each
        // caller sees the failure.
        private static readonly ConditionalWeakTable<IDbTable, ApprovalConfig> ConfigByTable = new();

        /// <summary>
        /// Parses the approval config for a single table (cached per table instance). Throws
        /// <see cref="InvalidOperationException"/> when <c>approval</c> is set to anything but
        /// <see cref="MetadataKeys.Approval.Enabled"/>, when <c>approval</c> is present without
        /// a required <see cref="MetadataKeys.Approval.ApproverRole"/> (a gate nobody can
        /// approve is fail-open), or when <c>self-approve</c> is an unrecognized token — each of
        /// which would otherwise leave a table that reads as gated in config either impossible
        /// to approve or silently ungated. No partial config: either a fully-resolved gate loads
        /// or the model fails to load.
        /// </summary>
        public static ApprovalConfig FromTable(IDbTable table)
        {
            if (table is null)
                throw new ArgumentNullException(nameof(table));

            return ConfigByTable.GetValue(table, Parse);
        }

        private static ApprovalConfig Parse(IDbTable table)
        {
            var approvalRaw = table.GetMetadataValue(MetadataKeys.Approval.Marker);
            if (string.IsNullOrWhiteSpace(approvalRaw))
                return None;

            if (!string.Equals(approvalRaw.Trim(), MetadataKeys.Approval.Enabled, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"'{MetadataKeys.Approval.Marker}' on '{table.TableSchema}.{table.DbName}' must be " +
                    $"'{MetadataKeys.Approval.Enabled}'; it is a plain opt-in switch, not a value.");

            var approverRole = table.GetMetadataValue(MetadataKeys.Approval.ApproverRole);
            if (string.IsNullOrWhiteSpace(approverRole))
                throw new InvalidOperationException(
                    $"'{MetadataKeys.Approval.Marker}' on '{table.TableSchema}.{table.DbName}' requires an " +
                    $"'{MetadataKeys.Approval.ApproverRole}': an approval gate with no approver can never be " +
                    $"approved (fail-open) and must not load. There is no default approver role.");

            var selfApprove = ParseSelfApprove(table);

            return new ApprovalConfig(requiresApproval: true, approverRole.Trim(), selfApprove);
        }

        private static bool ParseSelfApprove(IDbTable table)
        {
            var raw = table.GetMetadataValue(MetadataKeys.Approval.SelfApprove);
            if (string.IsNullOrWhiteSpace(raw))
                return true; // Default TRUE unless declared false.

            var token = raw.Trim();
            if (MetadataKeys.Approval.TrueValues.Contains(token))
                return true;
            if (MetadataKeys.Approval.FalseValues.Contains(token))
                return false;

            throw new InvalidOperationException(
                $"'{MetadataKeys.Approval.SelfApprove}' on '{table.TableSchema}.{table.DbName}' has an " +
                $"unrecognized value '{raw}'; use one of {string.Join(", ", MetadataKeys.Approval.TrueValues)} " +
                $"(true) or {string.Join(", ", MetadataKeys.Approval.FalseValues)} (false).");
        }
    }
}
