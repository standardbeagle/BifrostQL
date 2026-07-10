namespace BifrostQL.Core.Resolvers
{
    /// <summary>
    /// The mutation verb a request selects. <see cref="Sync"/> exists only on the
    /// single-row mutation field; the batch field carries no sync action.
    /// </summary>
    internal enum MutationAction
    {
        None,
        Sync,
        Insert,
        Update,
        Delete,
        Upsert,
    }

    /// <summary>
    /// Single source of truth for mutation-verb dispatch, consulted by both the
    /// single-row resolver (which verb argument is present on the GraphQL field) and
    /// the batch resolver (which verb key carries a data dictionary on a batch action).
    /// Keeping the verb order and names here stops the two dispatch chains from drifting.
    /// </summary>
    internal static class MutationActionSelector
    {
        /// <summary>
        /// Returns the mutation verb present on a single-row mutation field, checked in
        /// the same order the resolver historically used (sync → insert → update →
        /// delete → upsert), or <see cref="MutationAction.None"/> when no verb argument
        /// is present.
        /// </summary>
        public static MutationAction FromContext(IBifrostFieldContext context)
        {
            if (context.HasArgument("sync")) return MutationAction.Sync;
            if (context.HasArgument("insert")) return MutationAction.Insert;
            if (context.HasArgument("update")) return MutationAction.Update;
            if (context.HasArgument("delete")) return MutationAction.Delete;
            if (context.HasArgument("upsert")) return MutationAction.Upsert;
            return MutationAction.None;
        }

        /// <summary>
        /// Resolves a single batch action to its verb and the data dictionary it
        /// carries, checked in the resolver's historical order (insert → update →
        /// delete → upsert). Returns false (with <see cref="MutationAction.None"/>) when
        /// the action carries no recognized verb keyed to a dictionary.
        /// </summary>
        public static bool TryFromAction(
            Dictionary<string, object?> action,
            out MutationAction which,
            out Dictionary<string, object?> data)
        {
            if (action.TryGetValue("insert", out var insertObj) && insertObj is Dictionary<string, object?> insertData)
            {
                which = MutationAction.Insert;
                data = insertData;
                return true;
            }
            if (action.TryGetValue("update", out var updateObj) && updateObj is Dictionary<string, object?> updateData)
            {
                which = MutationAction.Update;
                data = updateData;
                return true;
            }
            if (action.TryGetValue("delete", out var deleteObj) && deleteObj is Dictionary<string, object?> deleteData)
            {
                which = MutationAction.Delete;
                data = deleteData;
                return true;
            }
            if (action.TryGetValue("upsert", out var upsertObj) && upsertObj is Dictionary<string, object?> upsertData)
            {
                which = MutationAction.Upsert;
                data = upsertData;
                return true;
            }
            which = MutationAction.None;
            data = new Dictionary<string, object?>();
            return false;
        }
    }
}
