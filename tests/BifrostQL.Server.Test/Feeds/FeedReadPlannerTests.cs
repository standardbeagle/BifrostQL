using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;
using BifrostQL.Server.Feeds;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace BifrostQL.Server.Test.Feeds
{
    /// <summary>
    /// The feed read planner: a programmatic <c>GqlObjectQuery</c> built only from cached-model names
    /// (no GraphQL/SQL text), executed exclusively through <c>IQueryIntentExecutor</c> so the
    /// transformer pipeline is unskippable, ordered newest-first with the FULL primary key as a
    /// deterministic tiebreak (composite-safe), bounded under the server maximum, and producing a
    /// deterministic item id from the whole key plus timestamp. Untrusted since/limit input collapses
    /// to a clean <c>FeedRequestException</c>.
    /// </summary>
    public sealed class FeedReadPlannerTests
    {
        private static readonly FeedOptions Options = new()
        {
            MaxItems = 10,
            DefaultItems = 5,
            Title = "My Feed",
            Link = "https://example.test/feed",
            Description = "A test feed",
            Author = "Feed Operator",
        };

        // ---- query shape / projection -----------------------------------------------------------

        [Fact]
        public void BuildQuery_projects_timestamp_body_template_and_full_primary_key_columns()
        {
            var table = FeedTableFixture.Posts();
            var query = FeedReadPlanner.BuildQuery(table, FeedConfig.FromTable(table), new FeedRequest(null, null), Options);

            query.ScalarColumns.Select(c => c.DbDbName)
                .Should().BeEquivalentTo(new[] { "published_at", "body", "title", "slug", "id" });
            query.DbTable.Should().BeSameAs(table);
            query.TableName.Should().Be("posts");
            query.SchemaName.Should().Be("dbo");
        }

        [Fact]
        public void BuildQuery_orders_timestamp_descending_then_primary_key_ascending_as_tiebreak()
        {
            var table = FeedTableFixture.Posts();
            var query = FeedReadPlanner.BuildQuery(table, FeedConfig.FromTable(table), new FeedRequest(null, null), Options);

            // Newest first, then the key ascending so rows with equal timestamps order deterministically.
            query.Sort.Should().Equal("published_at_desc", "id_asc");
        }

        [Fact]
        public void BuildQuery_emits_every_composite_key_component_in_projection_and_tiebreak()
        {
            var table = FeedTableFixture.CompositeKeyPosts();
            var query = FeedReadPlanner.BuildQuery(table, FeedConfig.FromTable(table), new FeedRequest(null, null), Options);

            // No first-key shortcut: both key components appear as tiebreaks and in the projection.
            query.Sort.Should().Equal("published_at_desc", "tenant_id_asc", "id_asc");
            query.ScalarColumns.Select(c => c.DbDbName).Should().Contain(new[] { "tenant_id", "id" });
        }

        // ---- bounds -----------------------------------------------------------------------------

        [Fact]
        public void BuildQuery_uses_the_default_limit_when_none_requested()
        {
            var table = FeedTableFixture.Posts();
            var query = FeedReadPlanner.BuildQuery(table, FeedConfig.FromTable(table), new FeedRequest(null, null), Options);

            query.Limit.Should().Be(Options.DefaultItems);
        }

        [Fact]
        public void BuildQuery_clamps_a_requested_limit_to_the_server_maximum()
        {
            var table = FeedTableFixture.Posts();
            var query = FeedReadPlanner.BuildQuery(table, FeedConfig.FromTable(table), new FeedRequest(null, 10_000), Options);

            query.Limit.Should().Be(Options.MaxItems);
        }

        // ---- since predicate (the ONLY adapter-built filter) ------------------------------------

        [Fact]
        public void BuildQuery_builds_no_filter_when_no_since_boundary()
        {
            var table = FeedTableFixture.Posts();
            var query = FeedReadPlanner.BuildQuery(table, FeedConfig.FromTable(table), new FeedRequest(null, null), Options);

            query.Filter.Should().BeNull("without a since boundary the adapter contributes no predicate; the pipeline scopes rows");
        }

        [Fact]
        public void BuildQuery_builds_a_single_gte_since_predicate_on_the_timestamp_column()
        {
            var table = FeedTableFixture.Posts();
            var since = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

            var query = FeedReadPlanner.BuildQuery(table, FeedConfig.FromTable(table), new FeedRequest(since, null), Options);

            query.Filter.Should().NotBeNull();
            // A lone predicate on the timestamp column: a Join node naming the column whose Next is
            // the _gte relation carrying the boundary as a bound parameter value — no AND/OR, no text.
            query.Filter!.ColumnName.Should().Be("published_at");
            query.Filter.And.Should().BeEmpty();
            query.Filter.Or.Should().BeEmpty();
            var relation = query.Filter.Next!;
            relation.RelationName.Should().Be(FilterOperators.Gte);
            relation.Value.Should().Be(since);
        }

        // ---- transformer intent seam ------------------------------------------------------------

        [Fact]
        public async Task BuildAsync_executes_through_the_query_intent_executor_carrying_identity_and_endpoint()
        {
            var table = FeedTableFixture.Posts();
            var reads = new CapturingReads();
            var planner = new FeedReadPlanner(reads);
            var userContext = new Dictionary<string, object?> { ["tenantId"] = "acme" };

            await planner.BuildAsync(table, new FeedRequest(null, 3), userContext, Options, endpoint: "/graphql");

            reads.Captured.Should().NotBeNull();
            reads.Captured!.UserContext.Should().BeSameAs(userContext);
            reads.Captured.Endpoint.Should().Be("/graphql");
            reads.Captured.Query.Filter.Should().BeNull("the seam must carry no adapter predicate beyond the declared since bound");
            reads.Captured.Query.Limit.Should().Be(3);
        }

        [Fact]
        public async Task BuildAsync_maps_rows_to_items_with_expanded_templates_and_body()
        {
            var table = FeedTableFixture.Posts();
            var reads = new CapturingReads
            {
                Rows = new[]
                {
                    Row(("id", 7), ("published_at", new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc)),
                        ("title", "Hello"), ("body", "<p>hi</p>"), ("slug", "hello")),
                },
            };
            var planner = new FeedReadPlanner(reads);

            var document = await planner.BuildAsync(table, new FeedRequest(null, null), Empty(), Options);

            var item = document.Items.Single();
            item.Title.Should().Be("Post: Hello");   // title template expanded from a schema column
            item.Link.Should().Be("/posts/hello");   // link template expanded
            item.Body.Should().Be("<p>hi</p>");      // raw; the writer escapes
            item.Timestamp.Should().Be(new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc));
            document.Updated.Should().Be(item.Timestamp);
            document.Title.Should().Be(Options.Title);
            document.Author.Should().Be(Options.Author);
        }

        // ---- deterministic item id --------------------------------------------------------------

        [Fact]
        public async Task Item_guid_is_deterministic_for_the_same_key_and_timestamp()
        {
            var table = FeedTableFixture.Posts();
            var ts = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
            var row = Row(("id", 42), ("published_at", ts), ("title", "T"), ("body", "B"), ("slug", "s"));

            var first = (await new FeedReadPlanner(new CapturingReads { Rows = new[] { row } })
                .BuildAsync(table, new FeedRequest(null, null), Empty(), Options)).Items.Single().Guid;
            var second = (await new FeedReadPlanner(new CapturingReads { Rows = new[] { row } })
                .BuildAsync(table, new FeedRequest(null, null), Empty(), Options)).Items.Single().Guid;

            second.Should().Be(first);
        }

        [Fact]
        public async Task Item_guid_differs_when_any_composite_key_component_differs()
        {
            var table = FeedTableFixture.CompositeKeyPosts();
            var ts = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);

            var a = await GuidFor(table, Row(("tenant_id", 1), ("id", 9), ("published_at", ts), ("title", "T"), ("body", "B"), ("slug", "s")));
            var b = await GuidFor(table, Row(("tenant_id", 2), ("id", 9), ("published_at", ts), ("title", "T"), ("body", "B"), ("slug", "s")));

            // Only tenant_id changed; if the id folded in just the first key component these would collide.
            b.Should().NotBe(a);
        }

        [Fact]
        public async Task Item_guid_fails_safely_on_a_null_key_component()
        {
            var table = FeedTableFixture.Posts();
            var ts = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
            var reads = new CapturingReads { Rows = new[] { Row(("id", null), ("published_at", ts), ("title", "T"), ("body", "B"), ("slug", "s")) } };

            var act = () => new FeedReadPlanner(reads).BuildAsync(table, new FeedRequest(null, null), Empty(), Options);

            await act.Should().ThrowAsync<FeedException>();
        }

        [Fact]
        public async Task Item_fails_safely_on_a_null_timestamp()
        {
            var table = FeedTableFixture.Posts();
            var reads = new CapturingReads { Rows = new[] { Row(("id", 1), ("published_at", null), ("title", "T"), ("body", "B"), ("slug", "s")) } };

            var act = () => new FeedReadPlanner(reads).BuildAsync(table, new FeedRequest(null, null), Empty(), Options);

            await act.Should().ThrowAsync<FeedException>();
        }

        // ---- request parsing (invariant 5) ------------------------------------------------------

        [Fact]
        public void Parse_normalizes_a_since_offset_to_utc()
        {
            var request = FeedRequest.Parse("2026-01-02T00:00:00+05:00", null);

            request.Since.Should().Be(new DateTime(2026, 1, 1, 19, 0, 0, DateTimeKind.Utc));
            request.Since!.Value.Kind.Should().Be(DateTimeKind.Utc);
        }

        [Theory]
        [InlineData("not-a-date")]
        [InlineData("99999999999999999999999999999-01-01")]
        public void Parse_rejects_a_malformed_since_cleanly(string since)
        {
            var act = () => FeedRequest.Parse(since, null);
            act.Should().Throw<FeedRequestException>();
        }

        [Theory]
        [InlineData("99999999999999999999999999999")] // 29 digits: overflows int, must not crash
        [InlineData("abc")]
        public void Parse_rejects_a_malformed_or_overflowing_limit_cleanly(string limit)
        {
            var act = () => FeedRequest.Parse(null, limit);
            act.Should().Throw<FeedRequestException>();
        }

        [Fact]
        public void Parse_rejects_a_negative_limit()
        {
            var act = () => FeedRequest.Parse(null, "-1");
            act.Should().Throw<FeedRequestException>();
        }

        // ---- helpers ----------------------------------------------------------------------------

        private static async Task<string> GuidFor(IDbTable table, IReadOnlyDictionary<string, object?> row)
        {
            var document = await new FeedReadPlanner(new CapturingReads { Rows = new[] { row } })
                .BuildAsync(table, new FeedRequest(null, null), Empty(), Options);
            return document.Items.Single().Guid;
        }

        private static IReadOnlyDictionary<string, object?> Row(params (string Key, object? Value)[] cells)
            => cells.ToDictionary(c => c.Key, c => c.Value);

        private static Dictionary<string, object?> Empty() => new();

        private sealed class CapturingReads : IQueryIntentExecutor
        {
            public QueryIntent? Captured { get; private set; }
            public IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows { get; init; }
                = Array.Empty<IReadOnlyDictionary<string, object?>>();

            public Task<IDbModel> GetModelAsync(string? endpoint = null) => throw new NotSupportedException();

            public Task<QueryIntentResult> ExecuteAsync(QueryIntent intent, CancellationToken cancellationToken = default)
            {
                Captured = intent;
                return Task.FromResult(new QueryIntentResult { Rows = Rows, Sql = string.Empty });
            }
        }
    }

    /// <summary>
    /// Builds substitute feed tables whose metadata mirrors the slice-1 <c>FeedConfig</c> contract:
    /// a normalized timestamp, a placeholder title/link template, and a body column, over single- or
    /// composite-key posts.
    /// </summary>
    internal static class FeedTableFixture
    {
        public static IDbTable Posts()
            => Build(keyOrder: new[] { "id" });

        public static IDbTable CompositeKeyPosts()
            => Build(keyOrder: new[] { "tenant_id", "id" });

        private static IDbTable Build(string[] keyOrder)
        {
            var keySet = new HashSet<string>(keyOrder, StringComparer.Ordinal);
            var columns = new List<ColumnDto>
            {
                Column("id", "int", isKey: true, nullable: false),
                Column("published_at", "datetime2"),
                Column("title", "nvarchar"),
                Column("body", "nvarchar"),
                Column("slug", "nvarchar"),
            };
            // A composite-key table adds tenant_id as a leading key component.
            if (keySet.Contains("tenant_id"))
                columns.Insert(0, Column("tenant_id", "int", isKey: true, nullable: false));

            var meta = new Dictionary<string, object?>
            {
                [MetadataKeys.Feed.Timestamp] = " PUBLISHED_AT ",
                [MetadataKeys.Feed.Title] = "Post: {title}",
                [MetadataKeys.Feed.Body] = "body",
                [MetadataKeys.Feed.Link] = "/posts/{slug}",
            };

            var table = Substitute.For<IDbTable>();
            table.DbName.Returns("posts");
            table.GraphQlName.Returns("posts");
            table.TableSchema.Returns("dbo");
            table.Columns.Returns(columns);
            table.ColumnLookup.Returns(columns.ToDictionary(c => c.ColumnName, c => c));
            table.GraphQlLookup.Returns(columns.ToDictionary(c => c.GraphQlName, c => c));
            table.Metadata.Returns(meta);
            table.GetMetadataValue(Arg.Any<string>())
                .Returns(ci => meta.TryGetValue(ci.Arg<string>(), out var v) ? v?.ToString() : null);
            // KeyColumns in the declared order (composite-key ordering must be preserved).
            var keyColumns = keyOrder.Select(name => columns.Single(c => c.ColumnName == name)).ToList();
            table.KeyColumns.Returns(keyColumns);
            return table;
        }

        private static ColumnDto Column(string name, string dataType, bool isKey = false, bool nullable = true)
            => new()
            {
                ColumnName = name,
                GraphQlName = name,
                DataType = dataType,
                OrdinalPosition = 1,
                IsPrimaryKey = isKey,
                IsNullable = nullable,
            };
    }
}
