using System.Globalization;
using System.Text.Json;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using GraphQL;
using BifrostQL.Model;
using BifrostQL.Sqlite;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BifrostQL.Core.Test.Sqlite;

/// <summary>
/// <c>transition-not-by</c>: a state-machine transition the row's author may not take. The
/// probe needed a C# transformer for "the approver is not the author"; this is the metadata
/// form. The author is the column the rule names, compared with the caller's audit actor
/// (<c>user-audit-key</c>), read inside the write's transaction.
/// </summary>
public sealed class StateTransitionDistinctActorTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_state_distinct_actor_test;Mode=Memory;Cache=Shared";
    private const string EndpointPath = "/graphql";
    private SqliteConnection _keepAlive = null!;

    private static readonly string[] Rules =
    {
        ":root { user-audit-key: user_id }",
        "main.claims { state-column: status; initial-state: draft; states: draft, submitted, approved; " +
            "transitions: draft->submitted|submitted->approved[manager]; transition-not-by: submitted->approved:created_by }",
        "main.claims.created_by { populate: created-by }",
    };

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();
        await using var cmd = new SqliteCommand(
            "DROP TABLE IF EXISTS claims; " +
            "CREATE TABLE claims (id INTEGER PRIMARY KEY, title TEXT NOT NULL, status TEXT NOT NULL, created_by TEXT NULL); " +
            "INSERT INTO claims (id, title, status, created_by) VALUES (1, 'by alice', 'submitted', 'alice'), " +
            "(2, 'legacy', 'submitted', NULL), (3, 'by a guid', 'submitted', '0f8fad5b-d9cb-469f-a165-70867728950e'), " +
            "(4, 'by employee 42', 'submitted', '42'), (5, 'by employee 42.5', 'submitted', '42.5'), " +
            "(6, 'by employee 1e17', 'submitted', '100000000000000000');",
            _keepAlive);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    [Fact]
    public async Task Author_MayNotTakeTheTransition_ButAnotherHolderOfTheRoleMay()
    {
        var byAuthor = () => Approve(1, "alice");
        await byAuthor.Should().ThrowAsync<BifrostExecutionError>().WithMessage("State transition is not permitted.");
        (await StatusAsync(1)).Should().Be("submitted");

        await Approve(1, "bob");
        (await StatusAsync(1)).Should().Be("approved");
    }

    [Fact]
    public async Task Author_SpelledInAnotherCase_IsStillTheAuthor()
    {
        // An IdP may issue the subject in another case than the stored author (a uuid column
        // reads back lowercase; an oid claim arrives uppercase). An ordinal compare reads them
        // as two people and lets the author approve their own row.
        var upperName = () => Approve(1, "ALICE");
        var upperGuid = () => Approve(3, "0F8FAD5B-D9CB-469F-A165-70867728950E");

        await upperName.Should().ThrowAsync<BifrostExecutionError>().WithMessage("State transition is not permitted.");
        await upperGuid.Should().ThrowAsync<BifrostExecutionError>().WithMessage("State transition is not permitted.");
        (await StatusAsync(1)).Should().Be("submitted");
        (await StatusAsync(3)).Should().Be("submitted");
    }

    [Fact]
    public async Task Author_AsANonIntegerJsonNumberClaim_IsRefused()
    {
        // A host auth context can carry the actor as a JSON number. 42.0 used to become the
        // decimal 42.0, whose text "42.0" differed from the stored author "42", so the author
        // approved their own row. A JSON number that is not an integer is not an actor.
        var fractional = () => Approve(4, JsonDocument.Parse("42.0").RootElement.Clone());
        var overflowing = () => Approve(4, JsonDocument.Parse("1e400").RootElement.Clone());

        await fractional.Should().ThrowAsync<BifrostExecutionError>().WithMessage(AuditMutationTransformer.NonIntegerActorMessage);
        await overflowing.Should().ThrowAsync<BifrostExecutionError>().WithMessage(AuditMutationTransformer.NonIntegerActorMessage);
        (await StatusAsync(4)).Should().Be("submitted");
    }

    [Fact]
    public async Task Author_AsANumericClaim_IsTheAuthorWhateverItsSpelling()
    {
        // A host-supplied CLR number names the author whether it is 42L or 42.0m.
        var asDecimal = () => Approve(4, 42.0m);
        var asLong = () => Approve(4, 42L);

        await asDecimal.Should().ThrowAsync<BifrostExecutionError>().WithMessage("State transition is not permitted.");
        await asLong.Should().ThrowAsync<BifrostExecutionError>().WithMessage("State transition is not permitted.");
        (await StatusAsync(4)).Should().Be("submitted");
    }

    [Fact]
    public async Task Author_AsADoubleClaimInExponentForm_IsStillTheAuthor()
    {
        // A double at or above 1e17 formats as "1E+17" under the invariant culture. Parsed with
        // NumberStyles.Number that text was no number, the compare fell to text, and the author
        // approved their own row.
        Convert.ToString(1e17d, CultureInfo.InvariantCulture).Should().Be("1E+17", "control: the actor's text is exponent form");
        var byAuthor = () => Approve(6, 1e17d);

        await byAuthor.Should().ThrowAsync<BifrostExecutionError>().WithMessage("State transition is not permitted.");
        (await StatusAsync(6)).Should().Be("submitted");

        await Approve(6, 1.5e17d);
        (await StatusAsync(6)).Should().Be("approved", "control: a different actor in exponent form may approve");
    }

    [Fact]
    public async Task Author_AsANumericClaim_ComparesUnderTheInvariantCulture()
    {
        // Under de-DE 42.5m formats as "42,5", which differed from the stored "42.5".
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        try
        {
            42.5m.ToString().Should().Be("42,5", "control: the ambient culture formats differently");
            var byAuthor = () => Approve(5, 42.5m);

            await byAuthor.Should().ThrowAsync<BifrostExecutionError>().WithMessage("State transition is not permitted.");
            (await StatusAsync(5)).Should().Be("submitted");
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }

        await Approve(5, 43L);
        (await StatusAsync(5)).Should().Be("approved", "control: a different actor may approve");
    }

    [Fact]
    public async Task ModelLoad_RefusesAnAuthorColumnTheServerDoesNotStamp()
    {
        // Without populate: created-by the client writes the author column itself, so the
        // author can name someone else and then approve their own row.
        var unstamped = () => LoadAsync("main.claims { state-column: status; initial-state: draft; states: draft, submitted, approved; " +
                                        "transitions: draft->submitted|submitted->approved; transition-not-by: submitted->approved:created_by }");

        await unstamped.Should().ThrowAsync<InvalidOperationException>().WithMessage("*created-by*");
    }

    [Fact]
    public async Task UnknownAuthor_OrAnonymousCaller_IsRefused()
    {
        // Separation cannot be shown when either side is unknown, so the transition fails closed.
        var legacyRow = () => Approve(2, "bob");
        var anonymous = () => Approve(1, userId: null);

        await legacyRow.Should().ThrowAsync<BifrostExecutionError>();
        await anonymous.Should().ThrowAsync<BifrostExecutionError>();
        (await StatusAsync(2)).Should().Be("submitted");
    }

    [Fact]
    public async Task ModelLoad_RefusesARuleForAnUndeclaredTransitionOrAMissingColumn()
    {
        var undeclared = () => LoadAsync("main.claims { state-column: status; initial-state: draft; states: draft, submitted, approved; " +
                                         "transitions: draft->submitted; transition-not-by: submitted->approved:created_by }");
        var missing = () => LoadAsync("main.claims { state-column: status; initial-state: draft; states: draft, submitted, approved; " +
                                      "transitions: draft->submitted; transition-not-by: draft->submitted:author }");

        await undeclared.Should().ThrowAsync<InvalidOperationException>();
        await missing.Should().ThrowAsync<InvalidOperationException>().WithMessage("*author*");
    }

    private static Task<IDbModel> LoadAsync(string rule)
        => new DbModelLoader(new SqliteDbConnFactory(ConnString), new MetadataLoader(new[] { rule })).LoadAsync();

    private static async Task Approve(int id, object? userId)
    {
        var context = new Dictionary<string, object?> { ["roles"] = new[] { "manager" } };
        if (userId is not null)
            context["user_id"] = userId;
        await BuildExecutor().ExecuteAsync(new MutationIntent
        {
            Table = "claims",
            Action = MutationIntentAction.Update,
            Data = new Dictionary<string, object?> { ["status"] = "approved" },
            PrimaryKey = new object?[] { id },
            UserContext = context,
            Endpoint = EndpointPath,
        });
    }

    private async Task<string> StatusAsync(int id)
    {
        await using var cmd = new SqliteCommand($"SELECT status FROM claims WHERE id = {id}", _keepAlive);
        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    private static MutationIntentExecutor BuildExecutor()
    {
        var pathCache = new PathCache<Inputs>();
        pathCache.AddLoader(EndpointPath, async () =>
        {
            var factory = new SqliteDbConnFactory(ConnString);
            var model = await new DbModelLoader(factory, new MetadataLoader(Rules)).LoadAsync();
            return new Inputs(new Dictionary<string, object?> { ["model"] = model, ["connFactory"] = factory });
        });
        return new MutationIntentExecutor(pathCache, new MutationTransformersWrap
        {
            Transformers = new IMutationTransformer[] { new StateMachineMutationTransformer(), new AuditMutationTransformer() },
        });
    }
}
