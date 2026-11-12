using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;

namespace BifrostQL.Core.Modules;

/// <summary>
/// Enforces table-level state-machine metadata on insert and update mutations.
/// </summary>
public sealed class StateMachineMutationTransformer : IMutationTransformer, IModuleNamed
{
    private const string InvalidTransitionMessage = "State transition is not permitted.";

    private readonly PolicyEvaluator _evaluator = new();

    public string ModuleName => "state-machine";

    // Security range, after policy action/column checks.
    public int Priority => 2;

    public bool AppliesTo(IDbTable table, MutationType mutationType, MutationTransformContext context)
    {
        if (table is null) throw new ArgumentNullException(nameof(table));
        return mutationType is MutationType.Insert or MutationType.Update
            && StateMachineConfigCollector.FromTable(table) is not null;
    }

    /// <summary>
    /// A <c>transition-not-by</c> rule reads the row's author inside the write's transaction, so
    /// such a table stays off the bulk batch path, where no transaction exists yet.
    /// </summary>
    public bool RequiresWriteTransaction(IDbTable table)
        => StateMachineConfigCollector.FromTable(table)?.Transitions.Any(t => t.DistinctActorColumn is not null) == true;

    public ValueTask<MutationTransformResult> TransformAsync(
        IDbTable table,
        MutationType mutationType,
        Dictionary<string, object?> data,
        MutationTransformContext context)
        => TransformCoreAsync(table, mutationType, data, context);

    private async ValueTask<MutationTransformResult> TransformCoreAsync(
        IDbTable table,
        MutationType mutationType,
        Dictionary<string, object?> data,
        MutationTransformContext context)
    {
        if (table is null) throw new ArgumentNullException(nameof(table));
        if (data is null) throw new ArgumentNullException(nameof(data));
        if (context is null) throw new ArgumentNullException(nameof(context));

        var definition = StateMachineConfigCollector.FromTable(table);
        if (definition is null || !TryGetValue(data, definition.StateColumn, out var newStateValue))
            return Allowed(mutationType, data);

        var newState = newStateValue?.ToString();
        if (string.IsNullOrWhiteSpace(newState) || !definition.States.Contains(newState))
            return Denied(mutationType, data);

        if (mutationType == MutationType.Insert)
        {
            return string.Equals(newState.Trim(), definition.InitialState, StringComparison.OrdinalIgnoreCase)
                ? Allowed(mutationType, data)
                : Denied(mutationType, data);
        }

        if (!TryGetValue(context.CurrentRow, definition.StateColumn, out var currentStateValue))
            return Denied(mutationType, data);

        var currentState = currentStateValue?.ToString();
        if (string.IsNullOrWhiteSpace(currentState))
            return Denied(mutationType, data);

        // From here on the update is allowed against the state it was validated on, so the
        // UPDATE must still find the row in THAT state: a concurrent transition between the
        // locked load and this write (or a dialect without a row lock) otherwise lets the
        // write land on a state it was never validated against. Same-state writes carry
        // the guard too — writing "open" over a row that just moved to "review" is a
        // review->open transition nobody validated.
        if (string.Equals(currentState.Trim(), newState.Trim(), StringComparison.OrdinalIgnoreCase))
            return AllowedFromState(table, mutationType, data, definition.StateColumn, currentStateValue);

        var transition = definition.Transitions.FirstOrDefault(t =>
            string.Equals(t.From, currentState.Trim(), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(t.To, newState.Trim(), StringComparison.OrdinalIgnoreCase));

        if (transition is null)
            return Denied(mutationType, data);

        var identity = StateMachineRoleGate.BuildIdentity(context.UserContext);
        if (!StateMachineRoleGate.CanUseTransition(transition, identity, _evaluator, PolicyConfigCollector.AdminRoleOf(table)))
            return Denied(mutationType, data);

        if (transition.DistinctActorColumn is { } authorColumn
            && !await IsDistinctActorAsync(table, data, context, authorColumn))
            return Denied(mutationType, data);

        return new MutationTransformResult
        {
            MutationType = mutationType,
            Data = data,
            AdditionalFilter = TableFilterFactory.Equals(table, definition.StateColumn, currentStateValue),
            ConflictOnNoRows = true,
            StateTransition = new StateTransitionInfo(
                table.DbName,
                ResolveEntityId(table, data),
                currentState.Trim(),
                newState.Trim(),
                identity.Id,
                transition.OnEvent ?? "StateTransitioned"),
        };
    }

    /// <summary>
    /// The caller's audit actor — the same "who" the created-by column is stamped with — differs
    /// from the row's <paramref name="authorColumn"/>. Read in the write's transaction under the
    /// update lock. Fails closed: no actor, no key, no row, or no recorded author all deny,
    /// because separation cannot be shown. The answer is the generic transition denial, so it
    /// reveals nothing about the row.
    /// </summary>
    private static async Task<bool> IsDistinctActorAsync(
        IDbTable table, Dictionary<string, object?> data, MutationTransformContext context, string authorColumn)
    {
        var actor = AuditMutationTransformer.ResolveActor(context.Model, context.UserContext)?.ToString();
        if (string.IsNullOrWhiteSpace(actor))
            return false;

        var key = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in table.KeyColumns)
        {
            if (!data.TryGetValue(column.ColumnName, out var value) || value is null)
                return false;
            key[column.ColumnName] = value;
        }
        if (key.Count == 0)
            return false;

        var row = await context.LoadRowForUpdateAsync(table, new[] { authorColumn }, key);
        var author = row is not null && row.TryGetValue(authorColumn, out var authorValue) ? authorValue?.ToString() : null;
        return !string.IsNullOrWhiteSpace(author) && !SameActor(author, actor);
    }

    /// <summary>
    /// Whether two actor spellings name one person. Uuids compare as values and everything
    /// else ignoring case: an IdP may issue the subject in another case than the column reads
    /// back, and erring toward "same" refuses a transition rather than allowing self-approval.
    /// </summary>
    internal static bool SameActor(string author, string actor)
        => Guid.TryParse(author, out var authorId) && Guid.TryParse(actor, out var actorId)
            ? authorId == actorId
            : string.Equals(author.Trim(), actor.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool TryGetValue(
        IReadOnlyDictionary<string, object?>? data,
        string key,
        out object? value)
    {
        value = null;
        return data is not null && data.TryGetValue(key, out value);
    }

    private static MutationTransformResult Allowed(MutationType mutationType, Dictionary<string, object?> data) =>
        new()
        {
            MutationType = mutationType,
            Data = data,
        };

    /// <summary>
    /// An allowed update re-checked at write time: <c>state = the loaded state</c> is ANDed
    /// into the UPDATE, and zero affected rows is a CONFLICT (the row left that state, or
    /// vanished, after it was read) rather than a silent no-op.
    /// </summary>
    private static MutationTransformResult AllowedFromState(
        IDbTable table, MutationType mutationType, Dictionary<string, object?> data,
        string stateColumn, object? loadedState) =>
        new()
        {
            MutationType = mutationType,
            Data = data,
            AdditionalFilter = TableFilterFactory.Equals(table, stateColumn, loadedState),
            ConflictOnNoRows = true,
        };

    private static MutationTransformResult Denied(MutationType mutationType, Dictionary<string, object?> data) =>
        new()
        {
            MutationType = mutationType,
            Data = data,
            Errors = new[] { InvalidTransitionMessage },
        };

    private static object? ResolveEntityId(IDbTable table, Dictionary<string, object?> data)
    {
        var key = table.KeyColumns?.FirstOrDefault();
        if (key is null)
            return null;

        if (data.TryGetValue(key.ColumnName, out var dbValue))
            return dbValue;

        return null;
    }
}
