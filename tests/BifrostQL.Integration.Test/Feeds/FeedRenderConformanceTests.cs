using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Integration.Test.Feeds
{
    /// <summary>
    /// End-to-end feed rendering over the REAL host/read pipeline (seeded SQLite → tenant-filter /
    /// soft-delete / policy transformers → <c>FeedReadPlanner</c> → RSS/Atom writers). Every assertion
    /// runs against the shipped registration, not a mock, so it proves the whole slice: required RSS 2.0
    /// and Atom structure via .NET XML APIs, newest-first ordering with a composite-PK tiebreak,
    /// soft-delete / tenant scoping, <c>since</c> and capped-limit behavior, and hostile row values that
    /// stay inert escaped text.
    /// </summary>
    public sealed class FeedRenderConformanceTests
    {
        // Tenant A live-row order, newest first, with the equal-timestamp (2026-05-03) pair broken by the
        // ascending composite key: id 1 (hostile) before id 2 (Gamma).
        private static readonly string[] TenantAOrder = { "Newest", FeedHost.HostileTitle, "Gamma", "Alpha" };

        [Fact]
        public async Task Rss_over_the_real_pipeline_has_required_structure_and_tenant_scoped_items()
        {
            await using var host = await FeedHost.StartAsync();

            var response = await host.GetAsync("/posts.rss", user: "u1", tenant: "A", roles: "admin");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Content.Headers.ContentType!.ToString().Should().Be("application/rss+xml; charset=utf-8");

            var doc = FeedXml.Parse(await response.Content.ReadAsStringAsync());
            doc.Root!.Name.LocalName.Should().Be("rss");
            doc.Root.Attribute("version")!.Value.Should().Be("2.0");

            var channel = FeedXml.Channel(doc);
            channel.Element("title")!.Value.Should().Be("Example Conformance Feed");
            channel.Element("link")!.Value.Should().Be("https://feeds.example.test/");
            channel.Element("description").Should().NotBeNull();

            // Only tenant A's live rows, newest-first with the composite-PK tiebreak; soft-deleted excluded.
            FeedXml.RssTitles(doc).Should().Equal(TenantAOrder);
            foreach (var item in FeedXml.RssItems(doc))
            {
                item.Element("guid")!.Attribute("isPermaLink")!.Value.Should().Be("false");
                item.Element("guid")!.Value.Should().NotBeNullOrWhiteSpace();
                item.Element("pubDate").Should().NotBeNull();
            }
        }

        [Fact]
        public async Task Atom_over_the_real_pipeline_has_required_structure()
        {
            await using var host = await FeedHost.StartAsync();

            var response = await host.GetAsync("/posts.atom", user: "u1", tenant: "A", roles: "admin");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Content.Headers.ContentType!.ToString().Should().Be("application/atom+xml; charset=utf-8");

            var doc = FeedXml.Parse(await response.Content.ReadAsStringAsync());
            doc.Root!.Name.Should().Be(FeedXml.Atom + "feed");

            // Required Atom feed-level elements (RFC 4287 §4.1.1): id, title, updated, author.
            doc.Root.Element(FeedXml.Atom + "id").Should().NotBeNull();
            doc.Root.Element(FeedXml.Atom + "title")!.Value.Should().Be("Example Conformance Feed");
            doc.Root.Element(FeedXml.Atom + "updated").Should().NotBeNull();
            doc.Root.Element(FeedXml.Atom + "author")!.Element(FeedXml.Atom + "name")!.Value.Should().Be("Example Operator");

            FeedXml.AtomTitles(doc).Should().Equal(TenantAOrder);
            foreach (var entry in FeedXml.AtomEntries(doc))
            {
                entry.Element(FeedXml.Atom + "id")!.Value.Should().StartWith("urn:uuid:");
                entry.Element(FeedXml.Atom + "updated").Should().NotBeNull();
            }
        }

        [Fact]
        public async Task Items_are_ordered_timestamp_desc_with_a_stable_composite_pk_tiebreak()
        {
            await using var host = await FeedHost.StartAsync();

            // Two requests must yield the identical ordering AND identical bytes — the same-timestamp pair
            // resolves by the ascending composite key every time, never by nondeterministic row arrival.
            var first = await (await host.GetAsync("/posts.rss", user: "u1", tenant: "A", roles: "admin")).Content.ReadAsStringAsync();
            var second = await (await host.GetAsync("/posts.rss", user: "u1", tenant: "A", roles: "admin")).Content.ReadAsStringAsync();

            first.Should().Be(second, "the feed must be deterministic — no wall-clock or arrival-order dependence");
            FeedXml.RssTitles(FeedXml.Parse(first)).Should().Equal(TenantAOrder);
        }

        [Fact]
        public async Task Tenant_filter_scopes_rows_to_the_caller_and_soft_deletes_never_appear()
        {
            await using var host = await FeedHost.StartAsync();

            var aDoc = FeedXml.Parse(await (await host.GetAsync("/posts.rss", user: "u1", tenant: "A", roles: "admin")).Content.ReadAsStringAsync());
            var bDoc = FeedXml.Parse(await (await host.GetAsync("/posts.rss", user: "u2", tenant: "B")).Content.ReadAsStringAsync());

            FeedXml.RssTitles(aDoc).Should().NotContain("Deleted Draft", "a soft-deleted row is filtered by the pipeline");
            FeedXml.RssTitles(aDoc).Should().HaveCount(4);
            FeedXml.RssTitles(bDoc).Should().ContainSingle("tenant B sees only its own single row").Which.Should().Be("Newest");
        }

        [Fact]
        public async Task Policy_denies_a_non_admin_read_but_an_admin_bypasses()
        {
            await using var host = await FeedHost.StartAsync();

            // bulletins grants policy-actions: update only — a read is denied for a non-admin caller and
            // collapses to the SAME sanitized 404 an unknown table gives (no existence oracle).
            var denied = await host.GetAsync("/bulletins.rss", user: "u1", tenant: "A");
            denied.StatusCode.Should().Be(HttpStatusCode.NotFound);

            var allowed = await host.GetAsync("/bulletins.rss", user: "admin1", tenant: "A", roles: "admin");
            allowed.StatusCode.Should().Be(HttpStatusCode.OK);
            FeedXml.RssTitles(FeedXml.Parse(await allowed.Content.ReadAsStringAsync())).Should().Equal("Ops Bulletin");
        }

        [Fact]
        public async Task Since_boundary_drops_older_items()
        {
            await using var host = await FeedHost.StartAsync();

            var doc = FeedXml.Parse(await (await host.GetAsync(
                "/posts.rss?since=2026-05-03T00:00:00Z", user: "u1", tenant: "A", roles: "admin")).Content.ReadAsStringAsync());

            // >= 2026-05-03 keeps the 05-04 and both 05-03 rows, drops 05-01 (Alpha).
            FeedXml.RssTitles(doc).Should().Equal("Newest", FeedHost.HostileTitle, "Gamma");
        }

        [Fact]
        public async Task Since_boundary_is_precise_to_the_second_not_just_the_date()
        {
            await using var host = await FeedHost.StartAsync();

            // One second past midnight on 05-03 must EXCLUDE both 05-03 00:00:00 rows and keep only 05-04.
            // This is the non-vacuous sub-day discriminator: it only holds because the stored TEXT format
            // matches the bound DateTime parameter's format lexically (an ISO "…T…Z" seed would wrongly
            // keep the 05-03 rows, since 'T' sorts after the bound value's space separator).
            var doc = FeedXml.Parse(await (await host.GetAsync(
                "/posts.rss?since=2026-05-03T00:00:01Z", user: "u1", tenant: "A", roles: "admin")).Content.ReadAsStringAsync());

            FeedXml.RssTitles(doc).Should().Equal("Newest");
        }

        [Fact]
        public async Task Capped_limit_bounds_item_count()
        {
            await using var host = await FeedHost.StartAsync(maxItems: 2, defaultItems: 2);

            // The server ceiling caps the default set to 2 (the two newest).
            var capped = FeedXml.Parse(await (await host.GetAsync("/posts.rss", user: "u1", tenant: "A", roles: "admin")).Content.ReadAsStringAsync());
            FeedXml.RssTitles(capped).Should().Equal("Newest", FeedHost.HostileTitle);

            // A requested limit under the ceiling is honored; a requested limit over it is clamped to 2.
            var one = FeedXml.Parse(await (await host.GetAsync("/posts.rss?limit=1", user: "u1", tenant: "A", roles: "admin")).Content.ReadAsStringAsync());
            FeedXml.RssTitles(one).Should().Equal("Newest");

            var over = FeedXml.Parse(await (await host.GetAsync("/posts.rss?limit=1000", user: "u1", tenant: "A", roles: "admin")).Content.ReadAsStringAsync());
            FeedXml.RssTitles(over).Should().HaveCount(2);
        }

        [Fact]
        public async Task Hostile_row_values_parse_as_xml_without_altering_document_structure()
        {
            await using var host = await FeedHost.StartAsync();

            var rss = await (await host.GetAsync("/posts.rss", user: "u1", tenant: "A", roles: "admin")).Content.ReadAsStringAsync();
            var atom = await (await host.GetAsync("/posts.atom", user: "u1", tenant: "A", roles: "admin")).Content.ReadAsStringAsync();

            // A strict XML parse would throw if the </item></channel><script> / ]]> payloads broke out.
            var rssDoc = FeedXml.Parse(rss);
            var atomDoc = FeedXml.Parse(atom);

            // Structure is intact: exactly the 4 authorized items/entries — the injected </item></channel>
            // did not truncate the channel and the injected <script> did not add a sibling element.
            FeedXml.RssItems(rssDoc).Should().HaveCount(4);
            FeedXml.AtomEntries(atomDoc).Should().HaveCount(4);
            FeedXml.Channel(rssDoc).Elements().Where(e => e.Name.LocalName == "script").Should().BeEmpty();

            // The hostile payload round-trips as inert TEXT: the title's VALUE equals the raw stored string
            // (the parser un-escaped it back to the original), proving it was escaped, not interpreted.
            FeedXml.RssTitles(rssDoc).Should().Contain(FeedHost.HostileTitle);
            FeedXml.AtomTitles(atomDoc).Should().Contain(FeedHost.HostileTitle);
        }

        [Fact]
        public async Task Malformed_since_or_limit_is_a_clean_400_for_an_authenticated_caller()
        {
            await using var host = await FeedHost.StartAsync();

            // Auth runs before request parsing, so these are authenticated: a malformed shape is the ONLY
            // reason for the 400 (the slice-4 FeedRequestException→400 e2e gap).
            (await host.GetAsync("/posts.rss?since=not-a-date", user: "u1", tenant: "A", roles: "admin"))
                .StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await host.GetAsync("/posts.rss?limit=abc", user: "u1", tenant: "A", roles: "admin"))
                .StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await host.GetAsync("/posts.rss?limit=99999999999999999999999999999", user: "u1", tenant: "A", roles: "admin"))
                .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
    }
}
