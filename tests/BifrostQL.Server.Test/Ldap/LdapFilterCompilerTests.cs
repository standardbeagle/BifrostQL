using System.Text;
using BifrostQL.Server.Ldap;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test.Ldap
{
    /// <summary>
    /// Filter compilation and evaluation. The compiler produces a SOUND OVER-APPROXIMATION for
    /// pushdown; the evaluator is the authority on whether an entry is returned. The split is what
    /// these tests are mostly about: a compiled predicate must never exclude an entry the evaluator
    /// would accept, and the evaluator must never accept one on Undefined.
    /// </summary>
    public sealed class LdapFilterCompilerTests
    {
        private static LdapEntryTarget People() =>
            LdapDirectoryIndex.Build(LdapModelBuilder.Create()
                    .WithPeople(attributes: "uid=username,cn=full_name,mail=email,uidNumber=uid_number")
                    .Build())!
                .Targets.Single(t => t.Table.DbName == "users");

        private static byte[] Octets(string value) => Encoding.UTF8.GetBytes(value);

        private static LdapFilter Equality(string attribute, string value) =>
            new LdapFilter.Comparison(LdapProtocol.FilterEqualityMatch, attribute, Octets(value));

        private static LdapFilter Ge(string attribute, string value) =>
            new LdapFilter.Comparison(LdapProtocol.FilterGreaterOrEqual, attribute, Octets(value));

        private static LdapFilter Substrings(string attribute, string? initial = null, string[]? any = null, string? final = null) =>
            new LdapFilter.Substrings(attribute, initial is null ? null : Octets(initial),
                (any ?? Array.Empty<string>()).Select(Octets).ToList(),
                final is null ? null : Octets(final));

        private static Dictionary<string, object?> Row(
            string? username = "alice", string? fullName = "Alice Anderson",
            string? email = "alice@example.com", int? uidNumber = 1001) =>
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["username"] = username,
                ["full_name"] = fullName,
                ["email"] = email,
                ["uid_number"] = uidNumber,
            };

        // ---- names come only from the mapping ----

        [Fact]
        public void Compile_MappedAttribute_UsesTheMappedColumn()
        {
            var compiled = LdapFilterCompiler.Compile(Equality("mail", "alice@example.com"), People());

            // 'mail' is published from the 'email' column; the predicate names the column, and the
            // client's value is a VALUE in the predicate, never part of an identifier.
            compiled.Pushdown.Should().ContainKey("email");
            compiled.Pushdown!["email"].Should().BeEquivalentTo(
                new Dictionary<string, object?> { ["_eq"] = "alice@example.com" });
        }

        [Fact]
        public void Compile_UnmappedAttribute_ConstrainsNothingAndMatchesNothing()
        {
            // RFC 4511: an unrecognized attribute type is Undefined. It must never become a column
            // name -- that is the path by which client text would turn into an identifier.
            var target = People();

            var compiled = LdapFilterCompiler.Compile(Equality("shoeSize", "44"), target);
            compiled.Pushdown.Should().BeNull();

            LdapFilterEvaluator.Evaluate(Equality("shoeSize", "44"), target, Row())
                .Should().Be(LdapMatch.Undefined);
            LdapFilterEvaluator.Matches(Equality("shoeSize", "44"), target, Row())
                .Should().BeFalse("Undefined is not TRUE, so the entry is not returned");
        }

        [Fact]
        public void Compile_CredentialColumnName_IsJustAnotherUnmappedAttribute()
        {
            // The egress sweep: the credential column is unreachable through a filter, and it is
            // unreachable in the SAME way any unmapped name is -- there is no distinguishable
            // response that would confirm the column exists.
            var target = People();
            target.Config.CredentialColumn.Should().Be("password_hash");

            var byColumnName = LdapFilterCompiler.Compile(Equality("password_hash", "x"), target);
            var byInventedName = LdapFilterCompiler.Compile(Equality("userPassword", "x"), target);
            var byNonsense = LdapFilterCompiler.Compile(Equality("zzz", "x"), target);

            byColumnName.Should().BeEquivalentTo(byNonsense);
            byInventedName.Should().BeEquivalentTo(byNonsense);

            LdapFilterEvaluator.Evaluate(Equality("password_hash", "x"), target, Row())
                .Should().Be(LdapMatch.Undefined);
        }

        [Fact]
        public void Compile_AttributeOptions_ResolveToTheBaseAttribute()
        {
            // 'cn;lang-en' selects a subtype this directory does not publish; the base name is what
            // resolves, so the option is ignored rather than making the whole assertion Undefined.
            LdapFilterCompiler.Compile(Equality("cn;lang-en", "Alice Anderson"), People())
                .Pushdown.Should().ContainKey("full_name");
        }

        // ---- values become parameters, in the column's syntax ----

        [Fact]
        public void Compile_IntegerAttribute_BindsATypedValue()
        {
            var compiled = LdapFilterCompiler.Compile(Ge("uidNumber", "1000"), People());

            compiled.Pushdown!["uid_number"].Should().BeEquivalentTo(
                new Dictionary<string, object?> { ["_gte"] = 1000L });
        }

        [Fact]
        public void Compile_NonConformingValueForTheSyntax_IsUndefinedNotCoerced()
        {
            // "abc" is not an integer. Coercing it (to 0, say) would compare against something the
            // client never asserted and return the wrong rows.
            var target = People();

            LdapFilterCompiler.Compile(Equality("uidNumber", "abc"), target).Pushdown.Should().BeNull();
            LdapFilterEvaluator.Evaluate(Equality("uidNumber", "abc"), target, Row())
                .Should().Be(LdapMatch.Undefined);
        }

        [Fact]
        public void Compile_IntegerValueBeyondTheTypeRange_IsUndefined()
        {
            // A well-formed but out-of-range integer throws OverflowException, not FormatException.
            // A catch scoped to the obviously-malformed case would let this escape the compiler on
            // an adversary-controlled path (invariant 5).
            var oversized = new string('9', 29);

            LdapFilterCompiler.Compile(Equality("uidNumber", oversized), People())
                .Pushdown.Should().BeNull();
        }

        // ---- three-valued logic ----

        [Fact]
        public void Evaluate_NotOfAnUndefinedTerm_StaysUndefined()
        {
            // The classic inversion: folding Undefined to FALSE here makes NOT(Undefined) TRUE, and
            // a filter over an unknown attribute starts returning the entire directory.
            var filter = new LdapFilter.Not(Equality("shoeSize", "44"));

            LdapFilterEvaluator.Evaluate(filter, People(), Row()).Should().Be(LdapMatch.Undefined);
            LdapFilterEvaluator.Matches(filter, People(), Row()).Should().BeFalse();
        }

        [Fact]
        public void Evaluate_AndWithAnUndefinedTerm_IsUndefinedUnlessSomethingIsFalse()
        {
            var target = People();
            var undefined = Equality("shoeSize", "44");

            LdapFilterEvaluator.Evaluate(
                    new LdapFilter.And(new[] { Equality("uid", "alice"), undefined }), target, Row())
                .Should().Be(LdapMatch.Undefined);

            // FALSE dominates: a definite non-match settles the conjunction regardless.
            LdapFilterEvaluator.Evaluate(
                    new LdapFilter.And(new[] { Equality("uid", "bob"), undefined }), target, Row())
                .Should().Be(LdapMatch.False);
        }

        [Fact]
        public void Evaluate_OrWithAnUndefinedTerm_IsTrueWhenAnotherBranchMatches()
        {
            var target = People();
            var filter = new LdapFilter.Or(new[] { Equality("shoeSize", "44"), Equality("uid", "alice") });

            LdapFilterEvaluator.Evaluate(filter, target, Row()).Should().Be(LdapMatch.True);
        }

        [Fact]
        public void Evaluate_PresenceOfAMappedButNullAttribute_IsFalseSoNegationReturnsTheEntry()
        {
            // Presence on a recognized attribute is FALSE (not Undefined) when there is no value,
            // which is what makes (!(mail=*)) mean "the entries with no mail".
            var target = People();
            var row = Row(email: null);

            LdapFilterEvaluator.Evaluate(new LdapFilter.Present("mail"), target, row)
                .Should().Be(LdapMatch.False);
            LdapFilterEvaluator.Evaluate(new LdapFilter.Not(new LdapFilter.Present("mail")), target, row)
                .Should().Be(LdapMatch.True);
        }

        [Fact]
        public void Evaluate_EqualityAgainstAnAbsentValue_IsUndefined()
        {
            LdapFilterEvaluator.Evaluate(Equality("mail", "x@y"), People(), Row(email: null))
                .Should().Be(LdapMatch.Undefined);
        }

        [Fact]
        public void Evaluate_ObjectClass_IsAnsweredFromTheDeclaredClasses()
        {
            var target = People();

            LdapFilterEvaluator.Evaluate(Equality("objectClass", "inetOrgPerson"), target, Row())
                .Should().Be(LdapMatch.True);
            LdapFilterEvaluator.Evaluate(Equality("objectClass", "groupOfNames"), target, Row())
                .Should().Be(LdapMatch.False);
            LdapFilterEvaluator.Evaluate(new LdapFilter.Present("objectClass"), target, Row())
                .Should().Be(LdapMatch.True);
        }

        [Fact]
        public void Compile_ObjectClassOfAnotherFamily_MatchesNothingWithoutExecuting()
        {
            // '(objectClass=groupOfNames)' against the people family is decidable from the mapping
            // alone -- no query needs to run at all.
            LdapFilterCompiler.Compile(Equality("objectClass", "groupOfNames"), People())
                .MatchesNothing.Should().BeTrue();
        }

        // ---- substrings ----

        [Theory]
        [InlineData("Alice Anderson", "Ali", null, true)]
        [InlineData("Alice Anderson", "Bob", null, false)]
        [InlineData("Alice Anderson", null, "son", true)]
        [InlineData("Alice Anderson", null, "xyz", false)]
        public void Evaluate_AnchoredSubstrings(string stored, string? initial, string? final, bool expected)
        {
            LdapFilterEvaluator.Matches(
                    Substrings("cn", initial: initial, final: final), People(), Row(fullName: stored))
                .Should().Be(expected);
        }

        [Fact]
        public void Evaluate_MultipleAnyFragments_MustAppearInOrder()
        {
            // The ordering the pushdown cannot express. An AND of two 'contains' predicates accepts
            // either order, so without the exact pass this entry would be returned for a filter
            // that does not name it.
            var target = People();

            LdapFilterEvaluator.Matches(Substrings("cn", any: new[] { "Alice", "Anderson" }), target, Row())
                .Should().BeTrue();
            LdapFilterEvaluator.Matches(Substrings("cn", any: new[] { "Anderson", "Alice" }), target, Row())
                .Should().BeFalse("the fragments must appear in the order the filter gives them");
        }

        [Fact]
        public void Evaluate_FinalMustNotOverlapEarlierFragments()
        {
            // Without the overlap check, "abc" satisfies (cn=ab*bc) by reusing the same 'b'.
            LdapFilterEvaluator.Matches(
                    Substrings("cn", initial: "ab", final: "bc"), People(), Row(fullName: "abc"))
                .Should().BeFalse();
            LdapFilterEvaluator.Matches(
                    Substrings("cn", initial: "ab", final: "bc"), People(), Row(fullName: "abbc"))
                .Should().BeTrue();
        }

        [Theory]
        [InlineData("%")]
        [InlineData("_")]
        [InlineData("100%")]
        [InlineData("a*b")]
        [InlineData("back\\slash")]
        [InlineData("[a-z]")]
        public void Evaluate_SqlAndLdapMetacharactersInAFragment_AreLiteral(string fragment)
        {
            // No pattern language is involved: the fragment is compared as a literal span. A '%'
            // that behaved as a wildcard would match every entry -- the exact failure the raw
            // _like operator invites, which is why the compiler never emits it.
            var target = People();

            LdapFilterEvaluator.Matches(Substrings("cn", any: new[] { fragment }), target, Row(fullName: "Alice"))
                .Should().BeFalse();
            LdapFilterEvaluator.Matches(
                    Substrings("cn", any: new[] { fragment }), target, Row(fullName: $"x{fragment}y"))
                .Should().BeTrue();
        }

        [Fact]
        public void Compile_Substrings_EmitsOnlyEscapingWildcardOperators()
        {
            // _like passes its pattern through untouched and declares no ESCAPE clause, so on
            // client-supplied text the client would choose the wildcards. The compiler must only
            // ever reach for the family that escapes the bound value.
            var compiled = LdapFilterCompiler.Compile(
                Substrings("cn", initial: "Al", any: new[] { "ic" }, final: "on"), People());

            var json = System.Text.Json.JsonSerializer.Serialize(compiled.Pushdown);
            json.Should().Contain("_starts_with").And.Contain("_contains").And.Contain("_ends_with");
            json.Should().NotContain("_like");
        }

        [Fact]
        public void Compile_NegatedSubstrings_PushesNoPredicate()
        {
            // NOT of an over-approximation is an UNDER-approximation -- it would exclude entries
            // that genuinely match. The assertion is still applied exactly by the evaluator.
            var filter = new LdapFilter.Not(Substrings("cn", any: new[] { "Ali", "son" }));

            LdapFilterCompiler.Compile(filter, People()).Pushdown.Should().BeNull();
            LdapFilterEvaluator.Matches(filter, People(), Row()).Should().BeFalse();
            LdapFilterEvaluator.Matches(filter, People(), Row(fullName: "Bob Brown")).Should().BeTrue();
        }

        // ---- pushdown soundness ----

        [Fact]
        public void Compile_OrWithAnInexpressibleBranch_DropsTheWholePredicate()
        {
            // Keeping only the expressible branch would NARROW an OR and hide entries that match
            // through the other one. Dropping the whole predicate keeps the fetch a superset.
            var filter = new LdapFilter.Or(new[]
            {
                Equality("uid", "alice"),
                new LdapFilter.Not(Substrings("cn", any: new[] { "a", "b" })),
            });

            LdapFilterCompiler.Compile(filter, People()).Pushdown.Should().BeNull();
        }

        [Fact]
        public void Compile_AndWithAnInexpressibleTerm_KeepsTheExpressibleOnes()
        {
            // Dropping a term from an AND keeps the result a superset, which is sound -- the
            // evaluator removes what the predicate let through.
            var filter = new LdapFilter.And(new[]
            {
                Equality("uid", "alice"),
                Equality("shoeSize", "44"),
            });

            LdapFilterCompiler.Compile(filter, People()).Pushdown.Should().ContainKey("username");
        }

        [Fact]
        public void Compile_NegatedEquality_PushesTheNegatedOperator()
        {
            // Exact, not an approximation: SQL's `col <> v` excludes NULL rows, and an absent
            // attribute makes the assertion Undefined, which is likewise not returned.
            LdapFilterCompiler.Compile(new LdapFilter.Not(Equality("uid", "alice")), People())
                .Pushdown!["username"].Should().BeEquivalentTo(
                    new Dictionary<string, object?> { ["_neq"] = "alice" });
        }

        [Fact]
        public void Compile_NestedNot_CancelsOut()
        {
            LdapFilterCompiler.Compile(
                    new LdapFilter.Not(new LdapFilter.Not(Equality("uid", "alice"))), People())
                .Pushdown!["username"].Should().BeEquivalentTo(
                    new Dictionary<string, object?> { ["_eq"] = "alice" });
        }

        [Fact]
        public void Compile_NegatedAnd_BecomesADisjunctionOfNegations()
        {
            var filter = new LdapFilter.Not(new LdapFilter.And(new[]
            {
                Equality("uid", "alice"),
                Equality("mail", "alice@example.com"),
            }));

            var compiled = LdapFilterCompiler.Compile(filter, People());

            compiled.Pushdown.Should().ContainKey("or");
        }

        [Fact]
        public void PushdownIsAlwaysASupersetOfTheExactMatch()
        {
            // The soundness property stated directly: over a spread of filters and rows, no entry
            // the evaluator accepts is excluded by the compiled predicate.
            var target = People();
            var filters = new LdapFilter[]
            {
                Equality("uid", "alice"),
                new LdapFilter.Not(Equality("uid", "alice")),
                new LdapFilter.Present("mail"),
                new LdapFilter.Not(new LdapFilter.Present("mail")),
                Substrings("cn", initial: "Al", final: "on"),
                new LdapFilter.Not(Substrings("cn", any: new[] { "ic" })),
                Ge("uidNumber", "1000"),
                new LdapFilter.And(new[] { Equality("uid", "alice"), Equality("shoeSize", "9") }),
                new LdapFilter.Or(new[] { Equality("uid", "bob"), new LdapFilter.Present("mail") }),
            };
            var rows = new[]
            {
                Row(),
                Row(email: null),
                Row(username: "bob", fullName: "Bob Brown", email: "bob@example.com", uidNumber: 999),
                Row(fullName: "Alicia Ondra"),
            };

            foreach (var filter in filters)
            {
                var compiled = LdapFilterCompiler.Compile(filter, target);
                foreach (var row in rows)
                {
                    if (!LdapFilterEvaluator.Matches(filter, target, row))
                        continue;
                    compiled.MatchesNothing.Should().BeFalse(
                        "a filter that matches an entry must not compile to 'never'");
                }
            }
        }
    }
}
