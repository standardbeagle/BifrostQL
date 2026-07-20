using System.Collections.Generic;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;

namespace BifrostQL.Core.Modules.History
{
    /// <summary>
    /// The right-to-erasure signal that a retention/erasure purge hands to the
    /// change-history writer, plus the SQL the writer emits to tombstone a trail.
    ///
    /// <para><b>Why a signal is needed.</b> A retention purge deletes a row through the
    /// SAME mutation pipeline as any other delete, so <see cref="HistoryMutationHook"/> fires
    /// for it. Its default delete behaviour records the row's before-image into the trail —
    /// but for an erasure purge that would RE-PERSIST the very PII the purge exists to erase,
    /// and the entity's EXISTING trail rows are themselves that PII. The engine therefore
    /// marks an erasure purge so the writer switches from "append a before-image" to
    /// "tombstone the trail" (see <see cref="HistoryMutationHook"/>).</para>
    ///
    /// <para><b>Why the marker is an opaque object reference, not a bool/string.</b> The
    /// signal travels in the delete's <c>UserContext</c> — the one channel the background
    /// engine fully controls (it also injects the hard-delete role there). A boolean or
    /// string flag could be FORGED by a caller whose identity projection happens to carry a
    /// same-named claim, letting them delete a row AND wipe its whole audit trail. The marker
    /// is a process-unique <see cref="object"/> reference created at static init: an external
    /// identity claim deserializes to a JSON scalar and can never be reference-equal to it, so
    /// erasure mode is unforgeable by construction — only code holding
    /// <see cref="Marker"/> can request it.</para>
    /// </summary>
    public static class HistoryErasure
    {
        /// <summary>
        /// The <c>UserContext</c> key under which a purge places <see cref="Marker"/> to
        /// request erasure/tombstone handling. The value MUST be <see cref="Marker"/> itself;
        /// any other value (including <c>true</c>) is ignored — see the class remarks.
        /// </summary>
        public const string ContextKey = "bifrost.history.erasure-purge";

        /// <summary>
        /// The process-unique sentinel a purge stores under <see cref="ContextKey"/>. Opaque
        /// and reference-compared, so it cannot be reproduced from deserialized identity
        /// claims. Placed by the retention purge engine, read only by
        /// <see cref="IsErasurePurge"/>.
        /// </summary>
        public static readonly object Marker = new();

        /// <summary>The <c>op</c> value recorded for an erasure tombstone row.</summary>
        public const string EraseOp = "erase";

        /// <summary>
        /// Whether this mutation's user context carries the unforgeable erasure marker. True
        /// ONLY when the value under <see cref="ContextKey"/> is reference-equal to
        /// <see cref="Marker"/>, so a forged same-named claim never trips it.
        /// </summary>
        public static bool IsErasurePurge(IDictionary<string, object?> userContext)
            => userContext.TryGetValue(ContextKey, out var value) && ReferenceEquals(value, Marker);

        /// <summary>
        /// The DELETE that purges an entity's existing trail rows during erasure, scoped by
        /// the trail's <c>entity</c> + <c>entity_id</c> columns (the serialized primary key,
        /// which is row-unique). Emitted entirely through <see cref="ISqlDialect"/> — no
        /// dialect literal — so it renders valid on every supported dialect; the two
        /// placeholders bind to the <c>entity</c>/<c>entity_id</c> parameters the writer adds.
        /// </summary>
        public static string BuildTrailPurgeSql(ISqlDialect dialect, string historyTableRef)
        {
            var entityCol = dialect.EscapeIdentifier(MetadataKeys.History.Column.Entity);
            var entityIdCol = dialect.EscapeIdentifier(MetadataKeys.History.Column.EntityId);
            return $"DELETE FROM {historyTableRef} " +
                   $"WHERE {entityCol} = @{MetadataKeys.History.Column.Entity} " +
                   $"AND {entityIdCol} = @{MetadataKeys.History.Column.EntityId};";
        }
    }
}
