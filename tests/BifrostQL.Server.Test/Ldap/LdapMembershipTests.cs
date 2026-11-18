using System.Text;
using BifrostQL.Core.Model;
using BifrostQL.Server.Ldap;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test.Ldap
{
    /// <summary>
    /// Group membership. The member and memberOf attributes are the one place this front door joins
    /// across tables, so they are the one place a scope boundary could be crossed by construction
    /// rather than by a missing check. Both join legs run as transformed query intents under the
    /// bound identity, which is what these tests exercise: a member the caller cannot see is not
    /// hidden from the response, it never arrives.
    /// </summary>
    public sealed class LdapMembershipTests
    {
        private static readonly DateTimeOffset Now = new(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);

        private static LdapSessionState Session(string tenant) => new()
        {
            TlsEstablished = true,
            Authenticated = true,
            UserContext = new Dictionary<string, object?> { ["sub"] = "caller", ["tenant"] = tenant },
        };

        private static LdapSearchRequest Search(
            string baseObject = "ou=groups,dc=example,dc=com",
            int scope = LdapSearchScope.SingleLevel,
            string[]? attributes = null) =>
            new(baseObject, scope, 0, 0, 0, false,
                new LdapFilter.Present("objectClass"),
                attributes ?? new[] { "cn", "member" });

        private static Dictionary<string, object?> Row(params (string Key, object? Value)[] values)
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in values)
                row[key] = value;
            return row;
        }

        // A directory of groups whose members are users, both tenant-scoped.
        private static (LdapSearchExecutor Executor, LdapFakeIntentExecutor Pipeline) BuildDirectory(
            Action<LdapWireOptions>? configure = null)
        {
            var builder = LdapModelBuilder.Create()
                .WithPeople()
                .WithGroups(memberRelationship: "members")
                .WithMembershipBridge();

            var pipeline = new LdapFakeIntentExecutor(builder.Build());
            var options = new LdapWireOptions
            {
                PagedResultsCookieSecret = "test-cookie-secret",
                MemberOfEnabled = true,
            };
            configure?.Invoke(options);
            return (new LdapSearchExecutor(pipeline, options, clock: () => Now), pipeline);
        }

        private static IReadOnlyList<string> MemberValues(LdapSearchResultEntry entry, string attribute = "member") =>
            entry.Attributes.FirstOrDefault(a =>
                string.Equals(a.Type, attribute, StringComparison.OrdinalIgnoreCase))?.Values
            ?? Array.Empty<string>();

        [Fact]
        public async Task Member_ValuesAreDnsBuiltFromTheTargetsOwnMapping()
        {
            var (executor, pipeline) = BuildDirectory();
            pipeline.WithRows("groups", Row(("id", 1), ("name", "admins"), ("description", "Admins")));
            pipeline.WithRows("users",
                Row(("id", 10), ("username", "alice"), ("full_name", "Alice"), ("email", "a@x")),
                Row(("id", 11), ("username", "bob"), ("full_name", "Bob"), ("email", "b@x")));
            pipeline.WithRows("user_groups",
                Row(("id", 1), ("group_id", 1), ("user_id", 10)),
                Row(("id", 2), ("group_id", 1), ("user_id", 11)));

            var outcome = await executor.ExecuteAsync(
                Search(), Array.Empty<LdapControl>(), Session("acme"), CancellationToken.None);

            var entry = outcome.Entries.Should().ContainSingle().Subject;
            MemberValues(entry).Should().BeEquivalentTo(new[]
            {
                "uid=alice,ou=people,dc=example,dc=com",
                "uid=bob,ou=people,dc=example,dc=com",
            });
        }

        [Fact]
        public async Task Member_BothJoinLegsRunAsTransformedIntentsUnderTheBoundIdentity()
        {
            // Not "a query happened" but "every query carried the identity the pipeline narrows
            // from". A leg that skipped it would be an unscoped read of a join table.
            var (executor, pipeline) = BuildDirectory();
            pipeline.WithRows("groups", Row(("id", 1), ("name", "admins")));
            pipeline.WithRows("users", Row(("id", 10), ("username", "alice")));
            pipeline.WithRows("user_groups", Row(("id", 1), ("group_id", 1), ("user_id", 10)));

            await executor.ExecuteAsync(
                Search(), Array.Empty<LdapControl>(), Session("acme"), CancellationToken.None);

            pipeline.Intents.Select(i => i.Query.TableName)
                .Should().Contain(new[] { "groups", "user_groups", "users" });
            pipeline.Intents.Should().OnlyContain(i => (string)i.UserContext["tenant"]! == "acme");
        }

        [Fact]
        public async Task Member_DoesNotNameAForeignTenantsEntryEvenWhenTheJunctionRowIsVisible()
        {
            // The leg-2 direction. The junction row is in the caller's tenant and genuinely points
            // at a user in another one. The member row does not come back, so no DN is built for
            // it -- the group simply reports the members the caller can see.
            var (executor, pipeline) = BuildDirectory();
            pipeline.WithRows("groups", Row(("id", 1), ("name", "admins"), ("tenant", "acme")));
            pipeline.WithRows("users",
                Row(("id", 10), ("username", "alice"), ("tenant", "acme")),
                Row(("id", 99), ("username", "foreigner"), ("tenant", "other")));
            pipeline.WithRows("user_groups",
                Row(("id", 1), ("group_id", 1), ("user_id", 10), ("tenant", "acme")),
                Row(("id", 2), ("group_id", 1), ("user_id", 99), ("tenant", "acme")));

            var outcome = await executor.ExecuteAsync(
                Search(), Array.Empty<LdapControl>(), Session("acme"), CancellationToken.None);

            var members = MemberValues(outcome.Entries.Single());
            members.Should().ContainSingle().Which.Should().Be("uid=alice,ou=people,dc=example,dc=com");
            members.Should().NotContain(dn => dn.Contains("foreigner"));
        }

        [Fact]
        public async Task Member_DoesNotNameAnEntryWhoseJunctionRowIsForeign()
        {
            // The leg-1 direction. The membership RECORD belongs to another tenant, so the caller
            // cannot observe the relationship at all -- even though the user it points at is one
            // the caller can otherwise see.
            var (executor, pipeline) = BuildDirectory();
            pipeline.WithRows("groups", Row(("id", 1), ("name", "admins"), ("tenant", "acme")));
            pipeline.WithRows("users",
                Row(("id", 10), ("username", "alice"), ("tenant", "acme")),
                Row(("id", 11), ("username", "bob"), ("tenant", "acme")));
            pipeline.WithRows("user_groups",
                Row(("id", 1), ("group_id", 1), ("user_id", 10), ("tenant", "acme")),
                Row(("id", 2), ("group_id", 1), ("user_id", 11), ("tenant", "other")));

            var outcome = await executor.ExecuteAsync(
                Search(), Array.Empty<LdapControl>(), Session("acme"), CancellationToken.None);

            MemberValues(outcome.Entries.Single()).Should()
                .ContainSingle().Which.Should().Be("uid=alice,ou=people,dc=example,dc=com");
        }

        [Fact]
        public async Task MemberOf_DoesNotNameAForeignTenantsGroup()
        {
            // The reverse direction, which needs its own coverage: a correct member join says
            // nothing about whether the memberOf join is scoped too.
            var (executor, pipeline) = BuildDirectory();
            pipeline.WithRows("groups",
                Row(("id", 1), ("name", "admins"), ("tenant", "acme")),
                Row(("id", 2), ("name", "secret-group"), ("tenant", "other")));
            pipeline.WithRows("users", Row(("id", 10), ("username", "alice"), ("tenant", "acme")));
            pipeline.WithRows("user_groups",
                Row(("id", 1), ("group_id", 1), ("user_id", 10), ("tenant", "acme")),
                Row(("id", 2), ("group_id", 2), ("user_id", 10), ("tenant", "acme")));

            var outcome = await executor.ExecuteAsync(
                Search(baseObject: "ou=people,dc=example,dc=com", attributes: new[] { "uid", "memberOf" }),
                Array.Empty<LdapControl>(), Session("acme"), CancellationToken.None);

            var groups = MemberValues(outcome.Entries.Single(), "memberOf");
            groups.Should().ContainSingle().Which.Should().Be("cn=admins,ou=groups,dc=example,dc=com");
            groups.Should().NotContain(dn => dn.Contains("secret-group"));
        }

        [Fact]
        public async Task MemberOf_IsAbsentUnlessTheDeploymentEnabledIt()
        {
            var (executor, pipeline) = BuildDirectory(o => o.MemberOfEnabled = false);
            pipeline.WithRows("groups", Row(("id", 1), ("name", "admins")));
            pipeline.WithRows("users", Row(("id", 10), ("username", "alice")));
            pipeline.WithRows("user_groups", Row(("id", 1), ("group_id", 1), ("user_id", 10)));

            var outcome = await executor.ExecuteAsync(
                Search(baseObject: "ou=people,dc=example,dc=com", attributes: new[] { "uid", "memberOf" }),
                Array.Empty<LdapControl>(), Session("acme"), CancellationToken.None);

            MemberValues(outcome.Entries.Single(), "memberOf").Should().BeEmpty();
        }

        [Fact]
        public async Task Member_IsNotResolvedWhenTheClientDidNotAskForIt()
        {
            // Membership is a fan-out of extra queries. Paying for it on a search that never
            // selected the attribute would make every unrelated search slower.
            var (executor, pipeline) = BuildDirectory();
            pipeline.WithRows("groups", Row(("id", 1), ("name", "admins")));
            pipeline.WithRows("user_groups", Row(("id", 1), ("group_id", 1), ("user_id", 10)));

            await executor.ExecuteAsync(
                Search(attributes: new[] { "cn" }), Array.Empty<LdapControl>(),
                Session("acme"), CancellationToken.None);

            pipeline.Intents.Select(i => i.Query.TableName).Should().NotContain("user_groups");
        }

        [Fact]
        public async Task Member_IsResolvedOnlyForTheRowsThePageEmits()
        {
            // Five matching groups, a size limit of one: only the emitted group's members may be
            // fetched. The junction leg's row ceiling is keys x MaxMembersPerEntry + 1, so it
            // names how many source rows the membership query was built for.
            var (executor, pipeline) = BuildDirectory(o => o.MaxMembersPerEntry = 3);
            for (var g = 1; g <= 5; g++)
            {
                pipeline.WithRows("groups", Row(("id", g), ("name", $"group{g}")));
                pipeline.WithRows("users", Row(("id", 100 + g), ("username", $"user{g}")));
                pipeline.WithRows("user_groups", Row(("id", g), ("group_id", g), ("user_id", 100 + g)));
            }
            var oneEntry = Search() with { SizeLimit = 1 };

            var outcome = await executor.ExecuteAsync(
                oneEntry, Array.Empty<LdapControl>(), Session("acme"), CancellationToken.None);

            var entry = outcome.Entries.Should().ContainSingle().Subject;
            MemberValues(entry).Should().ContainSingle();
            pipeline.Intents.Where(i => i.Query.TableName == "user_groups")
                .Should().ContainSingle().Which.Query.Limit.Should().Be(1 * 3 + 1,
                    "membership is resolved for the one emitted row, not the five matched");
        }

        [Fact]
        public async Task Member_OverThePerEntryBound_IsRefusedRatherThanTruncated()
        {
            // A truncated member list misreports who is in the group, which is worse than an
            // explicit refusal: a caller cannot tell it is incomplete.
            var (executor, pipeline) = BuildDirectory(o => o.MaxMembersPerEntry = 3);
            pipeline.WithRows("groups", Row(("id", 1), ("name", "everyone")));
            for (var i = 0; i < 10; i++)
            {
                pipeline.WithRows("users", Row(("id", 100 + i), ("username", $"user{i}")));
                pipeline.WithRows("user_groups", Row(("id", i), ("group_id", 1), ("user_id", 100 + i)));
            }

            var outcome = await executor.ExecuteAsync(
                Search(), Array.Empty<LdapControl>(), Session("acme"), CancellationToken.None);

            outcome.ResultCode.Should().Be(LdapResultCode.AdminLimitExceeded);
            outcome.Diagnostic.Should().BeEmpty();
        }

        [Fact]
        public async Task Member_PerEntryBoundIsCheckedPerEntryNotOnlyInAggregate()
        {
            // One oversized group must not ride in on a page of small ones. An aggregate-only check
            // would let it: the page's total stays under the combined ceiling.
            var (executor, pipeline) = BuildDirectory(o => o.MaxMembersPerEntry = 3);
            pipeline.WithRows("groups",
                Row(("id", 1), ("name", "small")),
                Row(("id", 2), ("name", "huge")));
            for (var i = 0; i < 5; i++)
            {
                pipeline.WithRows("users", Row(("id", 100 + i), ("username", $"user{i}")));
                pipeline.WithRows("user_groups", Row(("id", i), ("group_id", 2), ("user_id", 100 + i)));
            }

            var outcome = await executor.ExecuteAsync(
                Search(), Array.Empty<LdapControl>(), Session("acme"), CancellationToken.None);

            outcome.ResultCode.Should().Be(LdapResultCode.AdminLimitExceeded);
        }

        // Three groups of two members each: every group is under a per-entry bound of 3, and the
        // search resolves six member values in total.
        private static LdapFakeIntentExecutor SeedThreeGroupsOfTwo(LdapFakeIntentExecutor pipeline)
        {
            var link = 0;
            for (var g = 1; g <= 3; g++)
            {
                pipeline.WithRows("groups", Row(("id", g), ("name", $"group{g}")));
                for (var m = 0; m < 2; m++)
                {
                    var user = 100 + g * 10 + m;
                    pipeline.WithRows("users", Row(("id", user), ("username", $"user{user}")));
                    pipeline.WithRows("user_groups", Row(("id", ++link), ("group_id", g), ("user_id", user)));
                }
            }
            return pipeline;
        }

        [Fact]
        public async Task Member_OverThePerSearchTotal_IsRefusedEvenWhenEveryEntryIsUnderItsBound()
        {
            // The per-entry bound alone lets one search resolve page size x MaxMembersPerEntry
            // values (a million at the defaults). The per-search total binds on its own here: no
            // group exceeds 3 members, but the search would resolve 6 values against a total of 4.
            var (executor, pipeline) = BuildDirectory(o =>
            {
                o.MaxMembersPerEntry = 3;
                o.MaxMembershipValuesPerSearch = 4;
            });
            SeedThreeGroupsOfTwo(pipeline);

            var outcome = await executor.ExecuteAsync(
                Search(), Array.Empty<LdapControl>(), Session("acme"), CancellationToken.None);

            outcome.ResultCode.Should().Be(LdapResultCode.AdminLimitExceeded);
            outcome.Diagnostic.Should().BeEmpty();
            outcome.Entries.Should().BeEmpty("the search is refused, never answered with a truncated member list");
            pipeline.Intents.Where(i => i.Query.TableName == "user_groups")
                .Should().OnlyContain(i => i.Query.Limit <= 4 + 1,
                    "the junction read is bounded by what is left of the total before rows are consumed");
        }

        [Fact]
        public async Task Member_AtThePerSearchTotal_IsServed()
        {
            // Control for the fact above: the same fixture with the total at exactly 6 serves every
            // member, so the refusal there comes from the total and not from the fixture.
            var (executor, pipeline) = BuildDirectory(o =>
            {
                o.MaxMembersPerEntry = 3;
                o.MaxMembershipValuesPerSearch = 6;
            });
            SeedThreeGroupsOfTwo(pipeline);

            var outcome = await executor.ExecuteAsync(
                Search(), Array.Empty<LdapControl>(), Session("acme"), CancellationToken.None);

            outcome.ResultCode.Should().Be(LdapResultCode.Success);
            outcome.Entries.Should().HaveCount(3);
            outcome.Entries.Should().OnlyContain(e => MemberValues(e, "member").Count == 2);
        }

        [Fact]
        public async Task MemberOf_CountsAgainstThePerSearchTotal()
        {
            // The reverse hop draws on the same budget: six users, each in one group, against a
            // total of 4.
            var (executor, pipeline) = BuildDirectory(o =>
            {
                o.MaxMembersPerEntry = 3;
                o.MaxMembershipValuesPerSearch = 4;
            });
            SeedThreeGroupsOfTwo(pipeline);
            var people = Search(baseObject: "ou=people,dc=example,dc=com", attributes: new[] { "uid", "memberOf" });

            var outcome = await executor.ExecuteAsync(
                people, Array.Empty<LdapControl>(), Session("acme"), CancellationToken.None);

            outcome.ResultCode.Should().Be(LdapResultCode.AdminLimitExceeded);
        }

        [Fact]
        public async Task MemberOf_AtThePerSearchTotal_IsServed()
        {
            var (executor, pipeline) = BuildDirectory(o =>
            {
                o.MaxMembersPerEntry = 3;
                o.MaxMembershipValuesPerSearch = 6;
            });
            SeedThreeGroupsOfTwo(pipeline);
            var people = Search(baseObject: "ou=people,dc=example,dc=com", attributes: new[] { "uid", "memberOf" });

            var outcome = await executor.ExecuteAsync(
                people, Array.Empty<LdapControl>(), Session("acme"), CancellationToken.None);

            outcome.ResultCode.Should().Be(LdapResultCode.Success);
            outcome.Entries.Should().HaveCount(6);
            outcome.Entries.Should().OnlyContain(e => MemberValues(e, "memberOf").Count == 1);
        }

        // ---- the model's row ceiling below the membership bound ----

        // A directory whose max-query-rows (5) sits far below the per-entry bound (1000) and the
        // per-search total (10,000), so only the ceiling can cut a membership read.
        private static (LdapSearchExecutor Executor, LdapFakeIntentExecutor Pipeline) BuildCeilingDirectory()
        {
            var builder = LdapModelBuilder.Create()
                .WithPeople()
                .WithGroups(memberRelationship: "members")
                .WithMembershipBridge()
                .WithModelMetadata(MetadataKeys.Model.MaxQueryRows, 5);
            var pipeline = new LdapFakeIntentExecutor(builder.Build());
            var options = new LdapWireOptions { PagedResultsCookieSecret = "test-cookie-secret", MemberOfEnabled = true };
            return (new LdapSearchExecutor(pipeline, options, clock: () => Now), pipeline);
        }

        private static void SeedOneGroupOf(LdapFakeIntentExecutor pipeline, int members)
        {
            pipeline.WithRows("groups", Row(("id", 1), ("name", "everyone")));
            for (var i = 0; i < members; i++)
            {
                pipeline.WithRows("users", Row(("id", 100 + i), ("username", $"user{i}")));
                pipeline.WithRows("user_groups", Row(("id", i + 1), ("group_id", 1), ("user_id", 100 + i)));
            }
        }

        [Fact]
        public async Task Member_CutByTheModelRowCeiling_IsRefusedRatherThanTruncated()
        {
            // The junction read asked for LIMIT 1001 and the ceiling clamped it to 5. Its sentinel
            // row was the one clamped away, so "rows > bound" never fired and the group came back
            // with 5 of its 7 members under Success.
            var (executor, pipeline) = BuildCeilingDirectory();
            SeedOneGroupOf(pipeline, 7);

            var outcome = await executor.ExecuteAsync(
                Search(), Array.Empty<LdapControl>(), Session("acme"), CancellationToken.None);

            outcome.ResultCode.Should().Be(LdapResultCode.AdminLimitExceeded);
            outcome.Entries.Should().BeEmpty("the search is refused, never answered with a truncated member list");
        }

        [Fact]
        public async Task MemberOf_CutByTheModelRowCeiling_IsRefusedRatherThanTruncated()
        {
            // The reverse leg reads the same junction through the same window.
            var (executor, pipeline) = BuildCeilingDirectory();
            pipeline.WithRows("users", Row(("id", 10), ("username", "alice")));
            for (var g = 1; g <= 7; g++)
            {
                pipeline.WithRows("groups", Row(("id", g), ("name", $"group{g}")));
                pipeline.WithRows("user_groups", Row(("id", g), ("group_id", g), ("user_id", 10)));
            }
            var people = Search(baseObject: "ou=people,dc=example,dc=com", attributes: new[] { "uid", "memberOf" });

            var outcome = await executor.ExecuteAsync(
                people, Array.Empty<LdapControl>(), Session("acme"), CancellationToken.None);

            outcome.ResultCode.Should().Be(LdapResultCode.AdminLimitExceeded);
        }

        [Fact]
        public async Task Member_JunctionCutByTheCeiling_WithRepeatedMemberships_IsRefused()
        {
            // The facts above are also caught later, by the naming read: their 5 junction rows name
            // 5 distinct users, one more than that read's page. Here the 5 rows the ceiling lets
            // through all name the same user, so the naming read sees ONE key and is complete. Only
            // the junction leg's own ceiling check can tell that rows 6 and 7 (two more members)
            // were cut; without it the group was served with one member under Success.
            var (executor, pipeline) = BuildCeilingDirectory();
            pipeline.WithRows("groups", Row(("id", 1), ("name", "everyone")));
            foreach (var userId in new[] { 100, 101, 102 })
                pipeline.WithRows("users", Row(("id", userId), ("username", $"user{userId}")));
            var memberships = new[] { 100, 100, 100, 100, 100, 101, 102 };
            for (var i = 0; i < memberships.Length; i++)
                pipeline.WithRows("user_groups", Row(("id", i + 1), ("group_id", 1), ("user_id", memberships[i])));

            var outcome = await executor.ExecuteAsync(
                Search(), Array.Empty<LdapControl>(), Session("acme"), CancellationToken.None);

            outcome.ResultCode.Should().Be(LdapResultCode.AdminLimitExceeded);
            outcome.Entries.Should().BeEmpty("the search is refused, never answered with a truncated member list");
        }

        // A group whose members are its direct children (users.group_id), under the same
        // max-query-rows of 5 as BuildCeilingDirectory.
        private static (LdapSearchExecutor Executor, LdapSearchRequest Search) BuildDirectGroupOf(int members)
        {
            var builder = LdapModelBuilder.Create()
                .WithTable("users", t => t
                    .WithColumn("id", "int", isPrimaryKey: true)
                    .WithColumn("username", "nvarchar")
                    .WithColumn("group_id", "int")
                    .WithMetadata(MetadataKeys.Ldap.ObjectClass, "inetOrgPerson")
                    .WithMetadata(MetadataKeys.Ldap.DnTemplate, "uid={username},ou=people")
                    .WithMetadata(MetadataKeys.Ldap.Attributes, "uid=username"))
                .WithGroups(memberRelationship: "staff")
                .WithModelMetadata(MetadataKeys.Model.MaxQueryRows, 5);
            var groups = builder.Table("groups");
            var users = builder.Table("users");
            groups.MultiLinks["staff"] = new TableLinkDto
            {
                Name = "staff",
                ParentTable = groups,
                ChildTable = users,
                ParentId = groups.ColumnLookup["id"],
                ChildId = users.ColumnLookup["group_id"],
            };
            var pipeline = new LdapFakeIntentExecutor(builder.Build());
            var executor = new LdapSearchExecutor(
                pipeline, new LdapWireOptions { PagedResultsCookieSecret = "test-cookie-secret" }, clock: () => Now);
            pipeline.WithRows("groups", Row(("id", 1), ("name", "everyone")));
            for (var i = 0; i < members; i++)
                pipeline.WithRows("users", Row(("id", 100 + i), ("username", $"user{i}"), ("group_id", 1)));
            return (executor, Search(baseObject: "ou=groups,dc=example,dc=com", attributes: new[] { "cn", "member" }));
        }

        [Fact]
        public async Task Member_DirectOneToManyCutByTheCeiling_IsRefused()
        {
            // A direct one-to-many reads members and naming values in ONE query, so no later read
            // can notice the cut: the leg's own ceiling check is the only guard.
            var (executor, groupsOnly) = BuildDirectGroupOf(7);

            var outcome = await executor.ExecuteAsync(
                groupsOnly, Array.Empty<LdapControl>(), Session("acme"), CancellationToken.None);

            outcome.ResultCode.Should().Be(LdapResultCode.AdminLimitExceeded);
        }

        [Fact]
        public async Task Member_DirectOneToManyUnderTheCeiling_IsServedInFull()
        {
            // Control for the fact above: the same direct link with four members fits the page.
            var (executor, groupsOnly) = BuildDirectGroupOf(4);

            var outcome = await executor.ExecuteAsync(
                groupsOnly, Array.Empty<LdapControl>(), Session("acme"), CancellationToken.None);

            outcome.ResultCode.Should().Be(LdapResultCode.Success);
            MemberValues(outcome.Entries.Should().ContainSingle().Subject).Should().HaveCount(4);
        }

        [Fact]
        public async Task Member_UnderTheModelRowCeiling_IsServedInFull()
        {
            // Control: four members fit the ceiling's page (5 less the sentinel), so the refusal
            // above comes from the ceiling cut and not from the fixture.
            var (executor, pipeline) = BuildCeilingDirectory();
            SeedOneGroupOf(pipeline, 4);

            var outcome = await executor.ExecuteAsync(
                Search(), Array.Empty<LdapControl>(), Session("acme"), CancellationToken.None);

            outcome.ResultCode.Should().Be(LdapResultCode.Success);
            MemberValues(outcome.Entries.Should().ContainSingle().Subject).Should().HaveCount(4);
        }

        [Fact]
        public async Task Membership_legs_carry_a_total_order()
        {
            // A cap only means something over an ordered read: without ORDER BY the database may
            // hand back any 5 of 7 rows, and which ones it picks can change between calls.
            var (executor, pipeline) = BuildDirectory();
            SeedThreeGroupsOfTwo(pipeline);

            await executor.ExecuteAsync(
                Search(), Array.Empty<LdapControl>(), Session("acme"), CancellationToken.None);

            pipeline.Intents.Where(i => i.Query.TableName is "user_groups" or "users")
                .Should().NotBeEmpty()
                .And.OnlyContain(i => i.Query.Sort.Count > 0);
        }

        [Fact]
        public void Adapter_refuses_a_per_search_total_below_one()
        {
            var act = () => LdapWireAdapter.Validate(new LdapWireOptions { MaxMembershipValuesPerSearch = 0 });

            act.Should().Throw<ArgumentOutOfRangeException>()
                .Which.ParamName.Should().Be(nameof(LdapWireOptions.MaxMembershipValuesPerSearch));
        }

        [Fact]
        public async Task Member_CyclicMembershipTerminatesAndIsNotExpandedTransitively()
        {
            // Groups whose members are groups, wired into a cycle: A contains B, B contains A, and
            // C contains itself. Membership is resolved ONE hop -- transitive expansion is a
            // declared non-goal -- so the cycle cannot drive unbounded work. This is the property
            // that makes "no transitive expansion" a safety guarantee and not just a missing
            // feature.
            var builder = LdapModelBuilder.Create()
                .WithGroups(memberRelationship: "subgroups")
                .WithMembershipBridge(
                    linkName: "subgroups",
                    sourceTable: "groups", targetTable: "groups", junctionTable: "group_groups");

            var pipeline = new LdapFakeIntentExecutor(builder.Build());
            var executor = new LdapSearchExecutor(
                pipeline, new LdapWireOptions { PagedResultsCookieSecret = "s" }, clock: () => Now);

            pipeline.WithRows("groups",
                Row(("id", 1), ("name", "A")),
                Row(("id", 2), ("name", "B")),
                Row(("id", 3), ("name", "C")));
            pipeline.WithRows("group_groups",
                Row(("id", 1), ("group_id", 1), ("user_id", 2)),   // A -> B
                Row(("id", 2), ("group_id", 2), ("user_id", 1)),   // B -> A
                Row(("id", 3), ("group_id", 3), ("user_id", 3)));  // C -> C

            var outcome = await executor.ExecuteAsync(
                Search(), Array.Empty<LdapControl>(), Session("acme"), CancellationToken.None);

            outcome.ResultCode.Should().Be(LdapResultCode.Success);
            outcome.Entries.Should().HaveCount(3);

            MemberValues(outcome.Entries.Single(e => e.ObjectName.StartsWith("cn=A")))
                .Should().Equal("cn=B,ou=groups,dc=example,dc=com");
            MemberValues(outcome.Entries.Single(e => e.ObjectName.StartsWith("cn=B")))
                .Should().Equal("cn=A,ou=groups,dc=example,dc=com");
            // Self-membership resolves to itself once, and stops.
            MemberValues(outcome.Entries.Single(e => e.ObjectName.StartsWith("cn=C")))
                .Should().Equal("cn=C,ou=groups,dc=example,dc=com");
        }

        [Fact]
        public async Task Member_WithACompositeBridge_ResolvesNothingRatherThanJoiningOnAPartialKey()
        {
            // Model validation already refuses this configuration at startup. The resolver re-checks
            // so the guard does not depend on validation having run -- a first-column join here
            // would match rows agreeing on the group id while ignoring the tenant discriminator the
            // rest of the key carries, and would publish foreign entries as members.
            var builder = LdapModelBuilder.Create()
                .WithPeople()
                .WithGroups(memberRelationship: "members")
                .WithMembershipBridge();

            var model = builder.Build();
            var groups = builder.Table("groups");
            var link = groups.ManyToManyLinks["members"];
            groups.ManyToManyLinks["members"] = new ManyToManyLink
            {
                Name = link.Name,
                SourceTable = link.SourceTable,
                JunctionTable = link.JunctionTable,
                TargetTable = link.TargetTable,
                SourceColumn = link.SourceColumn,
                JunctionSourceColumn = link.JunctionSourceColumn,
                JunctionTargetColumn = link.JunctionTargetColumn,
                TargetColumn = link.TargetColumn,
                IsComposite = true,
            };

            var pipeline = new LdapFakeIntentExecutor(model);
            var executor = new LdapSearchExecutor(
                pipeline, new LdapWireOptions { PagedResultsCookieSecret = "s" }, clock: () => Now);

            pipeline.WithRows("groups", Row(("id", 1), ("name", "admins")));
            pipeline.WithRows("users", Row(("id", 10), ("username", "alice")));
            pipeline.WithRows("user_groups", Row(("id", 1), ("group_id", 1), ("user_id", 10)));

            var outcome = await executor.ExecuteAsync(
                Search(), Array.Empty<LdapControl>(), Session("acme"), CancellationToken.None);

            outcome.ResultCode.Should().Be(LdapResultCode.Success);
            MemberValues(outcome.Entries.Single()).Should().BeEmpty();
            pipeline.Intents.Select(i => i.Query.TableName).Should().NotContain("user_groups");
        }

        [Fact]
        public async Task Member_DnsAreEscapedLikeAnyOtherDn()
        {
            // A member DN is built from a column value, so it needs the same escaping as an entry's
            // own DN -- otherwise a comma in a name splits the member value into a DN naming a
            // different place in the tree.
            var (executor, pipeline) = BuildDirectory();
            pipeline.WithRows("groups", Row(("id", 1), ("name", "admins")));
            pipeline.WithRows("users", Row(("id", 10), ("username", "Doe, John")));
            pipeline.WithRows("user_groups", Row(("id", 1), ("group_id", 1), ("user_id", 10)));

            var outcome = await executor.ExecuteAsync(
                Search(), Array.Empty<LdapControl>(), Session("acme"), CancellationToken.None);

            MemberValues(outcome.Entries.Single()).Should()
                .Equal("uid=Doe\\, John,ou=people,dc=example,dc=com");
        }

        [Fact]
        public async Task Member_NeverExposesTheCredentialColumnThroughTheJoin()
        {
            // The join reaches a second table, so the credential sweep has to cover it too: the
            // member leg fetches only the key and the naming column.
            var (executor, pipeline) = BuildDirectory();
            pipeline.WithRows("groups", Row(("id", 1), ("name", "admins")));
            pipeline.WithRows("users",
                Row(("id", 10), ("username", "alice"), ("password_hash", "$2y$secret")));
            pipeline.WithRows("user_groups", Row(("id", 1), ("group_id", 1), ("user_id", 10)));

            var outcome = await executor.ExecuteAsync(
                Search(), Array.Empty<LdapControl>(), Session("acme"), CancellationToken.None);

            pipeline.Intents.SelectMany(i => i.Query.ScalarColumns.Select(c => c.DbDbName))
                .Should().NotContain("password_hash");
            outcome.Entries.SelectMany(e => e.Attributes).SelectMany(a => a.Values)
                .Should().NotContain(v => v.Contains("secret"));
        }
    }
}
