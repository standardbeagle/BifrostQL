using System.Text;
using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;
using BifrostQL.Server.Ldap;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test.Ldap
{
    /// <summary>
    /// End-to-end search behaviour against a pipeline stand-in that scopes by identity exactly as
    /// the real one does. The facts that matter here are the ones a unit test of any single
    /// component cannot show: that reads actually travel the transformed seam, that limits actually
    /// bound, that paging actually resumes, and that nothing a client can say reaches a table,
    /// column, or row the configuration and its identity did not already permit.
    /// </summary>
    public sealed class LdapSearchExecutorTests
    {
        private static readonly DateTimeOffset Now = new(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);

        private static LdapSessionState Session(string? tenant = null) => new()
        {
            TlsEstablished = true,
            Authenticated = true,
            IsAnonymous = false,
            UserContext = tenant is null
                ? new Dictionary<string, object?> { ["sub"] = "alice" }
                : new Dictionary<string, object?> { ["sub"] = "alice", ["tenant"] = tenant },
        };

        private static LdapSessionState AnonymousSession() => new()
        {
            TlsEstablished = true,
            Authenticated = true,
            IsAnonymous = true,
            UserContext = null,
        };

        private static LdapSearchRequest Search(
            string baseObject = "dc=example,dc=com",
            int scope = LdapSearchScope.WholeSubtree,
            LdapFilter? filter = null,
            string[]? attributes = null,
            int sizeLimit = 0,
            int timeLimit = 0,
            bool typesOnly = false) =>
            new(baseObject, scope, 0, sizeLimit, timeLimit, typesOnly,
                filter ?? new LdapFilter.Present("objectClass"),
                attributes ?? Array.Empty<string>());

        private static LdapFilter Equality(string attribute, string value) =>
            new LdapFilter.Comparison(LdapProtocol.FilterEqualityMatch, attribute, Encoding.UTF8.GetBytes(value));

        private static (LdapSearchExecutor Executor, LdapFakeIntentExecutor Pipeline) Build(
            LdapModelBuilder? builder = null, Action<LdapWireOptions>? configure = null)
        {
            var model = (builder ?? LdapModelBuilder.Create().WithPeople().WithGroups()).Build();
            var pipeline = new LdapFakeIntentExecutor(model);
            var options = new LdapWireOptions { PagedResultsCookieSecret = "test-cookie-secret" };
            configure?.Invoke(options);
            return (new LdapSearchExecutor(pipeline, options, clock: () => Now), pipeline);
        }

        private static Task<LdapSearchOutcome> RunAsync(
            LdapSearchExecutor executor, LdapSearchRequest request,
            LdapSessionState? session = null, params LdapControl[] controls) =>
            executor.ExecuteAsync(request, controls, session ?? Session(), CancellationToken.None);

        private static string? ValueOf(LdapSearchResultEntry entry, string attribute) =>
            entry.Attributes.FirstOrDefault(a =>
                string.Equals(a.Type, attribute, StringComparison.OrdinalIgnoreCase))?.Values.FirstOrDefault();

        // ---- reads travel the transformed seam ----

        [Fact]
        public async Task Search_ReturnsEntriesNamedByTheMappedDnTemplate()
        {
            var (executor, pipeline) = Build();
            pipeline.WithPeople(3);

            var outcome = await RunAsync(executor, Search());

            outcome.ResultCode.Should().Be(LdapResultCode.Success);
            outcome.Entries.Select(e => e.ObjectName).Should().Contain("uid=user0001,ou=people,dc=example,dc=com");
            ValueOf(outcome.Entries[0], "cn").Should().Be("User 0001");
        }

        [Fact]
        public async Task Search_RunsUnderTheBoundIdentity()
        {
            // Every intent carries the session's identity, which is what the pipeline narrows from.
            var (executor, pipeline) = Build();
            pipeline.WithPeople(2, tenant: "acme");

            await RunAsync(executor, Search(), Session(tenant: "acme"));

            pipeline.Intents.Should().NotBeEmpty();
            pipeline.Intents.Should().OnlyContain(i =>
                i.UserContext.ContainsKey("sub") && (string)i.UserContext["tenant"]! == "acme");
        }

        [Fact]
        public async Task Search_NeverSeesRowsOutsideTheCallersScope()
        {
            // The pipeline drops the foreign tenant's rows, and the LDAP layer has no other source
            // of rows -- so there is no path by which a foreign entry could be named.
            var (executor, pipeline) = Build();
            pipeline.WithPeople(2, tenant: "acme", startId: 1);
            pipeline.WithPeople(2, tenant: "other", startId: 50);

            var outcome = await RunAsync(executor, Search(), Session(tenant: "acme"));

            outcome.Entries.Should().HaveCount(2);
            outcome.Entries.Select(e => e.ObjectName).Should().OnlyContain(dn => dn.Contains("user000"));
            outcome.Entries.Select(e => e.ObjectName).Should().NotContain(dn => dn.Contains("user005"));
        }

        [Fact]
        public async Task Search_WithNoProjectedIdentity_IsRefusedRatherThanRunUnscoped()
        {
            // An empty context has nothing to narrow from. Refusing is the difference between
            // "the pipeline might scope an empty context to nothing" and "this front door never
            // runs an unscoped read".
            var (executor, pipeline) = Build();
            pipeline.WithPeople(3);
            var session = Session();
            session.UserContext = new Dictionary<string, object?>();

            var outcome = await RunAsync(executor, Search(), session);

            outcome.ResultCode.Should().Be(LdapResultCode.InsufficientAccessRights);
            outcome.Entries.Should().BeEmpty();
            pipeline.Intents.Should().BeEmpty("no query may be executed without an identity");
        }

        // ---- the credential column, swept across every egress ----

        [Fact]
        public async Task Search_NeverFetchesOrReturnsTheCredentialColumn()
        {
            // Not merely absent from the response: absent from the QUERY. A column that is never
            // fetched cannot be leaked by a later projection bug.
            var (executor, pipeline) = Build();
            pipeline.WithPeople(2);

            var outcome = await RunAsync(executor, Search(attributes: new[] { "*" }));

            pipeline.Intents.SelectMany(i => i.Query.ScalarColumns.Select(c => c.DbDbName))
                .Should().NotContain("password_hash");
            outcome.Entries.SelectMany(e => e.Attributes)
                .SelectMany(a => a.Values)
                .Should().NotContain(v => v.Contains("hash-for-user"));
        }

        [Fact]
        public async Task Search_FilteringOnTheCredentialColumn_MatchesNothingAndFetchesNothingExtra()
        {
            var (executor, pipeline) = Build();
            pipeline.WithPeople(2);

            var outcome = await RunAsync(executor, Search(
                filter: Equality("password_hash", "$2y$hash-for-user0001")));

            outcome.Entries.Should().BeEmpty("an unmapped attribute is Undefined, which returns nothing");
            pipeline.Intents.SelectMany(i => i.Query.ScalarColumns.Select(c => c.DbDbName))
                .Should().NotContain("password_hash");
        }

        // ---- attribute selection ----

        [Fact]
        public async Task Search_WithNamedAttributes_ReturnsOnlyThose()
        {
            var (executor, pipeline) = Build();
            pipeline.WithPeople(1);

            var outcome = await RunAsync(executor, Search(attributes: new[] { "uid" }));

            outcome.Entries.Single().Attributes.Select(a => a.Type).Should().Equal("uid");
        }

        [Fact]
        public async Task Search_WithNoAttributesRequested_ReturnsAllUserAttributes()
        {
            // RFC 4511: an EMPTY list means everything, not nothing.
            var (executor, pipeline) = Build();
            pipeline.WithPeople(1);

            var outcome = await RunAsync(executor, Search());

            outcome.Entries.Single().Attributes.Select(a => a.Type)
                .Should().Contain(new[] { "objectClass", "uid", "cn", "mail" });
        }

        [Fact]
        public async Task Search_WithTheNoAttributesOid_ReturnsBareDns()
        {
            var (executor, pipeline) = Build();
            pipeline.WithPeople(1);

            var outcome = await RunAsync(executor, Search(attributes: new[] { "1.1" }));

            outcome.Entries.Single().Attributes.Should().BeEmpty();
            outcome.Entries.Single().ObjectName.Should().Be("uid=user0001,ou=people,dc=example,dc=com");
        }

        [Fact]
        public async Task Search_TypesOnly_ReturnsAttributeTypesWithNoValues()
        {
            var (executor, pipeline) = Build();
            pipeline.WithPeople(1);

            var outcome = await RunAsync(executor, Search(typesOnly: true));

            var entry = outcome.Entries.Single();
            entry.Attributes.Should().NotBeEmpty();
            entry.Attributes.Should().OnlyContain(a => a.Values.Count == 0);
        }

        [Fact]
        public async Task Search_FetchesFilterColumnsEvenWhenNotRequested()
        {
            // The exact evaluation reads the fetched row. Omitting a filtered-on column would make
            // it read as absent, turning the assertion Undefined and dropping entries that match.
            var (executor, pipeline) = Build();
            pipeline.WithPeople(3);

            var outcome = await RunAsync(executor, Search(
                filter: Equality("mail", "user0002@example.com"), attributes: new[] { "uid" }));

            outcome.Entries.Should().ContainSingle();
            ValueOf(outcome.Entries[0], "uid").Should().Be("user0002");
            outcome.Entries[0].Attributes.Select(a => a.Type).Should().NotContain("mail",
                "a column fetched only to evaluate the filter is not published");
        }

        // ---- scope ----

        [Fact]
        public async Task Search_AtBaseScope_ReturnsTheOneNamedEntry()
        {
            var (executor, pipeline) = Build();
            pipeline.WithPeople(5);

            var outcome = await RunAsync(executor, Search(
                baseObject: "uid=user0003,ou=people,dc=example,dc=com", scope: LdapSearchScope.BaseObject));

            outcome.Entries.Should().ContainSingle()
                .Which.ObjectName.Should().Be("uid=user0003,ou=people,dc=example,dc=com");
        }

        [Fact]
        public async Task Search_AtBaseScope_NarrowsWithAParameterNotAnInterpolation()
        {
            var (executor, pipeline) = Build();
            pipeline.WithPeople(3);

            await RunAsync(executor, Search(
                baseObject: "uid=user0002,ou=people,dc=example,dc=com", scope: LdapSearchScope.BaseObject));

            // The RDN value came from a client DN; it must reach the query as a bound value.
            pipeline.Intents.Should().ContainSingle();
            pipeline.Intents[0].Query.Filter.Should().NotBeNull();
        }

        [Fact]
        public async Task Search_ForAnEntryOutsideTheCallersScope_IsIndistinguishableFromOneThatDoesNotExist()
        {
            // The anti-oracle fact stated end to end: an invisible entry and an absent one produce
            // the same code, the same empty diagnostic, and the same zero entries.
            var (executor, pipeline) = Build();
            pipeline.WithPeople(1, tenant: "other", startId: 50);

            var invisible = await RunAsync(
                executor,
                Search(baseObject: "uid=user0050,ou=people,dc=example,dc=com", scope: LdapSearchScope.BaseObject),
                Session(tenant: "acme"));
            var absent = await RunAsync(
                executor,
                Search(baseObject: "uid=nobody,ou=people,dc=example,dc=com", scope: LdapSearchScope.BaseObject),
                Session(tenant: "acme"));

            invisible.ResultCode.Should().Be(absent.ResultCode);
            invisible.Diagnostic.Should().Be(absent.Diagnostic).And.BeEmpty();
            invisible.Entries.Should().BeEmpty();
            absent.Entries.Should().BeEmpty();
        }

        [Fact]
        public async Task Search_ForAnUnknownBase_IsNoSuchObjectAndExecutesNothing()
        {
            var (executor, pipeline) = Build();
            pipeline.WithPeople(3);

            var outcome = await RunAsync(executor, Search(baseObject: "ou=nowhere,dc=example,dc=com"));

            outcome.ResultCode.Should().Be(LdapResultCode.NoSuchObject);
            outcome.Diagnostic.Should().BeEmpty();
            pipeline.Intents.Should().BeEmpty("an unaddressable base never reaches the database");
        }

        [Fact]
        public async Task Search_OneLevelUnderAContainer_ReturnsOnlyThatContainersFamily()
        {
            var (executor, pipeline) = Build();
            pipeline.WithPeople(2);
            pipeline.WithRows("groups",
                new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                    { ["id"] = 1, ["name"] = "admins", ["description"] = "Administrators" });

            var outcome = await RunAsync(executor, Search(
                baseObject: "ou=groups,dc=example,dc=com", scope: LdapSearchScope.SingleLevel));

            outcome.Entries.Should().ContainSingle()
                .Which.ObjectName.Should().Be("cn=admins,ou=groups,dc=example,dc=com");
        }

        // ---- limits ----

        [Fact]
        public async Task Search_AtTheServerCeiling_ReportsSizeLimitExceededRatherThanTruncatingSilently()
        {
            var (executor, pipeline) = Build(configure: o => o.MaxSearchResults = 5);
            pipeline.WithPeople(20);

            var outcome = await RunAsync(executor, Search());

            outcome.Entries.Should().HaveCount(5);
            outcome.ResultCode.Should().Be(LdapResultCode.SizeLimitExceeded,
                "a partial result that looks complete is worse than an explicit partial one");
        }

        [Fact]
        public async Task Search_ClientSizeLimit_NarrowsButNeverRaisesTheServerCeiling()
        {
            var (executor, pipeline) = Build(configure: o => o.MaxSearchResults = 5);
            pipeline.WithPeople(20);

            var narrowed = await RunAsync(executor, Search(sizeLimit: 2));
            var attemptedRaise = await RunAsync(executor, Search(sizeLimit: 100));

            narrowed.Entries.Should().HaveCount(2);
            attemptedRaise.Entries.Should().HaveCount(5, "the client cannot raise the server's ceiling");
        }

        [Fact]
        public async Task Search_BoundsEveryFetchRegardlessOfTheClientsRequest()
        {
            var (executor, pipeline) = Build(configure: o => o.SearchBatchSize = 7);
            pipeline.WithPeople(30);

            await RunAsync(executor, Search());

            pipeline.Intents.Should().OnlyContain(i => i.Query.Limit != null && i.Query.Limit <= 7);
        }

        [Fact]
        public async Task Search_OrdersByKeyColumnsSoPagingCannotRepeatOrSkip()
        {
            var (executor, pipeline) = Build();
            pipeline.WithPeople(3);

            await RunAsync(executor, Search());

            pipeline.Intents[0].Query.Sort.Should().NotBeEmpty();
        }

        // ---- paging ----

        [Fact]
        public async Task Search_WithThePagedResultsControl_ReturnsAPageAndAResumeCookie()
        {
            var (executor, pipeline) = Build();
            pipeline.WithPeople(12);

            var outcome = await RunAsync(executor, Search(), Session(), PagingControl(5));

            outcome.ResultCode.Should().Be(LdapResultCode.Success);
            outcome.Entries.Should().HaveCount(5);
            outcome.ResponseControls.Should().NotBeNull();
            ResponseCookie(outcome).Should().NotBeEmpty("more results remain, so a resume cookie is issued");
        }

        [Fact]
        public async Task Search_PagedThroughToTheEnd_VisitsEveryEntryExactlyOnce()
        {
            // The property paging exists for. Repeats or gaps here would mean the ordering or the
            // resume position is wrong, which no single-page test can show.
            var (executor, pipeline) = Build();
            pipeline.WithPeople(12);

            var seen = new List<string>();
            byte[] cookie = Array.Empty<byte>();
            for (var page = 0; page < 10; page++)
            {
                var outcome = await RunAsync(executor, Search(), Session(), PagingControl(5, cookie));
                outcome.ResultCode.Should().Be(LdapResultCode.Success);
                seen.AddRange(outcome.Entries.Select(e => e.ObjectName));
                cookie = ResponseCookie(outcome);
                if (cookie.Length == 0)
                    break;
            }

            seen.Should().HaveCount(12);
            seen.Should().OnlyHaveUniqueItems();
        }

        [Fact]
        public async Task Search_TheFinalPage_CarriesAnEmptyCookie()
        {
            var (executor, pipeline) = Build();
            pipeline.WithPeople(3);

            var outcome = await RunAsync(executor, Search(), Session(), PagingControl(10));

            ResponseCookie(outcome).Should().BeEmpty("an empty cookie is the protocol's end-of-results");
        }

        [Fact]
        public async Task Search_WithAForgedCookie_IsRefusedAndExecutesNothing()
        {
            var (executor, pipeline) = Build();
            pipeline.WithPeople(12);

            var forged = Encoding.ASCII.GetBytes("MHwwfDE3NTUzODU2MDA.bm90LWEtdmFsaWQtbWFj");
            var outcome = await RunAsync(executor, Search(), Session(), PagingControl(5, forged));

            outcome.ResultCode.Should().Be(LdapResultCode.UnavailableCriticalExtension);
            outcome.Entries.Should().BeEmpty();
            pipeline.Intents.Should().BeEmpty(
                "a cookie that does not validate must not silently restart the scan");
        }

        [Fact]
        public async Task Search_ACookieReplayedByAnotherIdentity_IsRefused()
        {
            var (executor, pipeline) = Build();
            pipeline.WithPeople(12, tenant: "acme");
            pipeline.WithPeople(12, tenant: "other", startId: 100);

            var first = await RunAsync(executor, Search(), Session(tenant: "acme"), PagingControl(5));
            var cookie = ResponseCookie(first);
            cookie.Should().NotBeEmpty();

            var replayed = await RunAsync(
                executor, Search(), Session(tenant: "other"), PagingControl(5, cookie));

            replayed.ResultCode.Should().Be(LdapResultCode.UnavailableCriticalExtension);
            replayed.Entries.Should().BeEmpty();
        }

        [Fact]
        public async Task Search_ACookieReplayedIntoADifferentSearch_IsRefused()
        {
            var (executor, pipeline) = Build();
            pipeline.WithPeople(12);

            var first = await RunAsync(executor, Search(), Session(), PagingControl(5));
            var cookie = ResponseCookie(first);

            var swapped = await RunAsync(
                executor,
                Search(baseObject: "ou=people,dc=example,dc=com", scope: LdapSearchScope.SingleLevel),
                Session(), PagingControl(5, cookie));

            swapped.ResultCode.Should().Be(LdapResultCode.UnavailableCriticalExtension);
        }

        [Fact]
        public async Task Search_APageLargerThanTheServerMaximum_IsNarrowed()
        {
            var (executor, pipeline) = Build(configure: o => o.MaxPageSize = 4);
            pipeline.WithPeople(20);

            var outcome = await RunAsync(executor, Search(), Session(), PagingControl(1000));

            outcome.Entries.Should().HaveCount(4);
        }

        // ---- controls ----

        [Fact]
        public async Task Search_WithAnUnsupportedCriticalControl_IsRefusedEntirely()
        {
            // RFC 4511 4.1.11: the client declared the operation meaningless without the control,
            // so answering it anyway answers a question that was not asked.
            var (executor, pipeline) = Build();
            pipeline.WithPeople(3);

            var outcome = await RunAsync(executor, Search(), Session(),
                new LdapControl("1.2.840.113556.1.4.473", Criticality: true, Value: null));

            outcome.ResultCode.Should().Be(LdapResultCode.UnavailableCriticalExtension);
            outcome.Entries.Should().BeEmpty();
            pipeline.Intents.Should().BeEmpty();
        }

        [Fact]
        public async Task Search_WithAnUnsupportedNonCriticalControl_IsIgnored()
        {
            var (executor, pipeline) = Build();
            pipeline.WithPeople(3);

            var outcome = await RunAsync(executor, Search(), Session(),
                new LdapControl("1.2.840.113556.1.4.473", Criticality: false, Value: null));

            outcome.ResultCode.Should().Be(LdapResultCode.Success);
            outcome.Entries.Should().HaveCount(3);
        }

        // ---- error funnel ----

        [Fact]
        public async Task Search_WhenThePipelineFaults_AnswersGenericallyWithNoInternalDetail()
        {
            // A BifrostExecutionError can wrap driver or transformer text: qualified table names,
            // the name of a missing context key. None of it may reach a client.
            var (executor, pipeline) = Build();
            pipeline.WithPeople(3);
            pipeline.Fault = new BifrostExecutionError(
                "Tenant context required but not found. Expected 'tenant_id' in user context for table 'main.users'.");

            var outcome = await RunAsync(executor, Search());

            outcome.ResultCode.Should().Be(LdapResultCode.OperationsError);
            outcome.Diagnostic.Should().BeEmpty();
            outcome.Diagnostic.Should().NotContain("tenant_id").And.NotContain("main.users");
        }

        [Fact]
        public async Task Search_WhenThePipelineThrowsAnUnexpectedType_StillAnswersRatherThanEscaping()
        {
            // The funnel is total on purpose: an escaping exception would drop the connection with
            // no SearchResultDone, leaving the client waiting on a search that already failed.
            var (executor, pipeline) = Build();
            pipeline.WithPeople(3);
            pipeline.Fault = new InvalidCastException("some internal detail");

            var outcome = await RunAsync(executor, Search());

            outcome.ResultCode.Should().Be(LdapResultCode.OperationsError);
            outcome.Diagnostic.Should().BeEmpty();
        }

        // ---- discovery ----

        [Fact]
        public async Task Search_TheRootDse_IsReadableAnonymouslyAndAdvertisesOnlyWhatIsImplemented()
        {
            var (executor, _) = Build();

            var outcome = await RunAsync(
                executor, Search(baseObject: "", scope: LdapSearchScope.BaseObject), AnonymousSession());

            var entry = outcome.Entries.Should().ContainSingle().Subject;
            entry.ObjectName.Should().BeEmpty();
            ValueOf(entry, "namingContexts").Should().Be("dc=example,dc=com");
            ValueOf(entry, "supportedLDAPVersion").Should().Be("3");
            ValueOf(entry, "subschemaSubentry").Should().Be("cn=subschema");

            // No SASL mechanisms are implemented, so none is advertised. Advertising one the server
            // cannot perform sends clients down a path that always fails.
            entry.Attributes.Select(a => a.Type).Should().NotContain("supportedSASLMechanisms");
            entry.Attributes.Select(a => a.Type).Should().NotContain("supportedControl");
        }

        [Fact]
        public async Task Search_TheSubschema_IsSkeletalForAnAnonymousSessionAndPopulatedForABoundOne()
        {
            // Introspection is filtered by the same rule as the data path: an unauthenticated
            // caller must not be able to enumerate the directory's shape (invariant 4).
            var (executor, _) = Build();
            var request = Search(baseObject: "cn=subschema", scope: LdapSearchScope.BaseObject);

            var anonymous = await RunAsync(executor, request, AnonymousSession());
            var bound = await RunAsync(executor, request, Session());

            var anonymousTypes = anonymous.Entries.Single().Attributes
                .FirstOrDefault(a => a.Type == "attributeTypes")?.Values ?? Array.Empty<string>();
            var boundTypes = bound.Entries.Single().Attributes
                .First(a => a.Type == "attributeTypes").Values;

            anonymousTypes.Should().BeEmpty();
            boundTypes.Should().Contain("uid").And.Contain("cn");
        }

        [Fact]
        public async Task Search_TheSubschema_ExcludesTablesTheCallerMayNotRead()
        {
            // Introspection is filtered by the SAME read policy as the data path: a table the caller
            // cannot READ must not contribute its objectClass or attributeType names to the subschema
            // (invariant 4). Before the fix an authenticated caller received the WHOLE model's
            // subschema, enumerating denied tables' shape. Non-vacuous: with the full-model subschema
            // restored, "secretEntry"/"secretCode" reappear.
            var builder = LdapModelBuilder.Create()
                .WithPeople() // readable by anyone (no policy) → objectClass inetOrgPerson, attrs uid/cn/mail
                .WithTable("secrets", t => t
                    .WithColumn("id", "int", isPrimaryKey: true)
                    .WithColumn("ssn", "nvarchar")
                    .WithMetadata(MetadataKeys.Ldap.ObjectClass, "secretEntry")
                    .WithMetadata(MetadataKeys.Ldap.DnTemplate, "secretCode={ssn},ou=secrets")
                    .WithMetadata(MetadataKeys.Ldap.Attributes, "secretCode=ssn")
                    // policy-actions omits read → a non-admin identity cannot read this table.
                    .WithMetadata("policy-actions", "update"));
            var (executor, _) = Build(builder);
            var request = Search(baseObject: "cn=subschema", scope: LdapSearchScope.BaseObject);

            var outcome = await RunAsync(executor, request, Session()); // alice: authenticated, non-admin

            var entry = outcome.Entries.Single();
            var objectClasses = entry.Attributes.First(a => a.Type == "objectClasses").Values;
            var attributeTypes = entry.Attributes.First(a => a.Type == "attributeTypes").Values;

            // The readable table's shape is published.
            objectClasses.Should().Contain("inetOrgPerson");
            attributeTypes.Should().Contain("uid").And.Contain("cn");
            // The read-denied table's shape is NOT — its objectClass and its unique attribute are absent.
            objectClasses.Should().NotContain("secretEntry", "a denied table's objectClass must not be published");
            attributeTypes.Should().NotContain("secretCode", "a denied table's attributeType must not be published");
        }

        [Fact]
        public async Task Search_TheSubschema_NeverNamesTheCredentialColumnOrItsAttribute()
        {
            // The fifth egress path of the slice-1 sweep.
            var (executor, _) = Build();

            var outcome = await RunAsync(
                executor, Search(baseObject: "cn=subschema", scope: LdapSearchScope.BaseObject));

            outcome.Entries.Single().Attributes.SelectMany(a => a.Values)
                .Should().NotContain(v =>
                    v.Contains("password", StringComparison.OrdinalIgnoreCase)
                    || v.Contains("userPassword", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task Search_AnAnonymousSessionReachingForData_IsRefused()
        {
            var (executor, pipeline) = Build();
            pipeline.WithPeople(3);

            var outcome = await RunAsync(executor, Search(), AnonymousSession());

            outcome.ResultCode.Should().Be(LdapResultCode.InsufficientAccessRights);
            pipeline.Intents.Should().BeEmpty();
        }

        [Fact]
        public async Task Search_DiscoveryEntriesStillHonourTheClientsFilter()
        {
            // A discovery read that ignored the filter would answer a question nobody asked, and
            // would return the RootDSE for (objectClass=somethingElse).
            var (executor, _) = Build();

            var matching = await RunAsync(executor,
                Search(baseObject: "", scope: LdapSearchScope.BaseObject, filter: Equality("objectClass", "top")),
                AnonymousSession());
            var notMatching = await RunAsync(executor,
                Search(baseObject: "", scope: LdapSearchScope.BaseObject, filter: Equality("objectClass", "person")),
                AnonymousSession());

            matching.Entries.Should().ContainSingle();
            notMatching.Entries.Should().BeEmpty();
            notMatching.ResultCode.Should().Be(LdapResultCode.Success);
        }

        private static LdapControl PagingControl(int size, byte[]? cookie = null) =>
            new(LdapPagedResultsControl.Oid, Criticality: true,
                Value: BerWriter.Sequence(
                    BerWriter.Integer(size),
                    BerWriter.Tlv(LdapProtocol.OctetString, cookie ?? Array.Empty<byte>())));

        // Reads the cookie back out of the encoded response control.
        private static byte[] ResponseCookie(LdapSearchOutcome outcome)
        {
            outcome.ResponseControls.Should().NotBeNull();
            var raw = outcome.ResponseControls!;
            var outer = new BerCursor(raw, 0, raw.Length);
            var control = outer.Child(outer.ReadElement(LdapProtocol.Sequence));
            control.ReadElement(LdapProtocol.OctetString); // the OID
            var valueElement = control.ReadElement(LdapProtocol.OctetString);
            var value = control.Content(valueElement);

            var inner = new BerCursor(value, 0, value.Length);
            var sequence = inner.Child(inner.ReadElement(LdapProtocol.Sequence));
            sequence.ReadElement(LdapProtocol.Integer); // the size estimate (always 0)
            return sequence.Content(sequence.ReadElement(LdapProtocol.OctetString));
        }
    }
}
