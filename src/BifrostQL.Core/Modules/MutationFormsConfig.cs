using BifrostQL.Core.Model;

namespace BifrostQL.Core.Modules;

/// <summary>One write form a table's generated mutation surface can offer.</summary>
[Flags]
public enum MutationForms
{
    None = 0,
    Insert = 1 << 0,
    Update = 1 << 1,
    Upsert = 1 << 2,
    Delete = 1 << 3,
    Sync = 1 << 4,
    Save = 1 << 5,
    Delta = 1 << 6,
    PrimaryKey = 1 << 7,
    Batch = 1 << 8,

    /// <summary>The row forms a <c>_batch</c> action can carry.</summary>
    BatchRowForms = Insert | Update | Upsert | Delete,

    /// <summary>The row forms a <c>delta</c> can carry (inserted, updated, deleted).</summary>
    DeltaRowForms = Insert | Update | Delete,

    All = Insert | Update | Upsert | Delete | Sync | Save | Delta | PrimaryKey | Batch,
}

/// <summary>
/// The one reading of <c>:root { mutation-forms: … }</c>
/// (<see cref="MetadataKeys.Model.MutationForms"/>): which write forms every table's generated
/// mutation surface offers. The schema generator reads it to emit exactly the listed arguments,
/// the <c>&lt;table&gt;_batch</c> field only when <c>batch</c> is listed, and only the listed forms'
/// input types; ModelConfigValidator calls the same parse at load. Absent, every form is offered.
/// Server-side writes (an operation's context, <c>IMutationIntentExecutor</c>) are not affected.
/// </summary>
public static class MutationFormsConfig
{
    private static readonly IReadOnlyDictionary<string, MutationForms> Tokens =
        new Dictionary<string, MutationForms>(StringComparer.OrdinalIgnoreCase)
        {
            ["insert"] = MutationForms.Insert,
            ["update"] = MutationForms.Update,
            ["upsert"] = MutationForms.Upsert,
            ["delete"] = MutationForms.Delete,
            ["sync"] = MutationForms.Sync,
            ["save"] = MutationForms.Save,
            ["delta"] = MutationForms.Delta,
            ["_primaryKey"] = MutationForms.PrimaryKey,
            ["batch"] = MutationForms.Batch,
        };

    /// <summary>
    /// The forms the model offers. Throws <see cref="InvalidOperationException"/> for an unknown
    /// token, a list naming no write form, or a <c>batch</c> / <c>delta</c> listed with none of
    /// the row forms it carries (its input would be an empty object, an invalid schema).
    /// </summary>
    public static MutationForms FromModel(IDbModel model)
    {
        var raw = model.GetMetadataValue(MetadataKeys.Model.MutationForms);
        if (raw is null)
            return MutationForms.All;

        var forms = MutationForms.None;
        foreach (var token in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Tokens.TryGetValue(token, out var form))
                throw new InvalidOperationException(
                    $"'{token}' is not a mutation form (expected {string.Join(", ", Tokens.Keys)}).");
            forms |= form;
        }

        if ((forms & ~MutationForms.PrimaryKey) == MutationForms.None)
            throw new InvalidOperationException("the list names no write form, so no table could be written.");
        if (forms.HasFlag(MutationForms.Batch) && (forms & MutationForms.BatchRowForms) == MutationForms.None)
            throw new InvalidOperationException(
                "'batch' carries the listed row forms (insert, update, upsert, delete), and none is listed.");
        if (forms.HasFlag(MutationForms.Delta) && (forms & MutationForms.DeltaRowForms) == MutationForms.None)
            throw new InvalidOperationException(
                "'delta' carries the listed row forms (insert, update, delete), and none is listed.");
        return forms;
    }
}
