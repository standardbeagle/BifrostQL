using System;
using System.Collections.Generic;
using System.Linq;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules.ComputedColumns;
using BifrostQL.Core.QueryModel.TestFixtures;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Model
{
    /// <summary>
    /// Locks the metadata rule-string grammar against the four defects fixed in
    /// <see cref="MetadataLoader"/>:
    ///   #1 braces reserved — values could not carry '{ }' (blocked policy-row-scope),
    ///   #2 colon truncation — values silently lost everything after the first ':',
    ///   #3 malformed rules silently no-op'd instead of failing fast,
    ///   #4 identifier regex metacharacters were injected unescaped.
    /// </summary>
    public class MetadataLoaderGrammarTests
    {
        private static IDictionary<string, object?> ApplyToSchema(string rule, string schema = "dbo")
        {
            var props = new Dictionary<string, object?>();
            new MetadataLoader(new[] { rule }).ApplyDatabaseMetadata(props, schema);
            return props;
        }

        // ---- #1 Braces in values --------------------------------------------

        [Fact]
        public void PropertyValue_MayContainBraces()
        {
            // Arrange: a row-scope expression carrying a {placeholder}.
            var props = ApplyToSchema("dbo { policy-row-scope: user_id = {user_id} }");

            // Assert: the braces survive verbatim.
            props.Should().ContainKey(MetadataKeys.Policy.RowScope)
                .WhoseValue.Should().Be("user_id = {user_id}");
        }

        [Fact]
        public void PropertyValue_MayContainMultipleBracedPlaceholders()
        {
            var props = ApplyToSchema("dbo { policy-row-scope: org = {tenant_id} and owner = {user_id} }");

            props[MetadataKeys.Policy.RowScope].Should().Be("org = {tenant_id} and owner = {user_id}");
        }

        // ---- #2 Colon truncation --------------------------------------------

        [Fact]
        public void PropertyValue_MayContainColons()
        {
            // Arrange: many-to-many uses "Target:Junction"; the ':' must survive.
            var props = ApplyToSchema("dbo { many-to-many: Roles:UserRoles }");

            props[MetadataKeys.Relationships.ManyToMany].Should().Be("Roles:UserRoles");
        }

        [Fact]
        public void PropertyValue_KeepsEverythingAfterFirstColon()
        {
            // computed-sql format is "name:GraphQlType:expr".
            var props = ApplyToSchema("dbo { computed-sql: full:String:{first} + {last} }");

            props[MetadataKeys.Computed.Sql].Should().Be("full:String:{first} + {last}");
        }

        [Fact]
        public void MultipleProperties_SplitOnSemicolonNotColon()
        {
            var props = ApplyToSchema("dbo { many-to-many: Roles:UserRoles; label: My Table }");

            props[MetadataKeys.Relationships.ManyToMany].Should().Be("Roles:UserRoles");
            props[MetadataKeys.Ui.Label].Should().Be("My Table");
        }

        // ---- #3 Malformed rules fail fast -----------------------------------

        [Theory]
        [InlineData("dbo tenant-filter: tenant_id")] // no braces at all
        [InlineData("dbo { tenant-filter: tenant_id")] // missing closing brace
        [InlineData("dbo tenant-filter: tenant_id }")] // missing opening brace
        public void MalformedRule_Throws(string rule)
        {
            Action act = () => new MetadataLoader(new[] { rule });

            act.Should().Throw<ArgumentException>().WithMessage("*Invalid metadata rule*");
        }

        [Fact]
        public void RuleWithNoSelector_Throws()
        {
            Action act = () => new MetadataLoader(new[] { "   { tenant-filter: tenant_id }" });

            act.Should().Throw<ArgumentException>();
        }

        [Fact]
        public void ValuelessProperty_ThrowsClearError_NotIndexOutOfRange()
        {
            Action act = () => new MetadataLoader(new[] { "dbo { hidden }" });

            act.Should().Throw<ArgumentException>()
                .WithMessage("*Invalid metadata property 'hidden'*");
        }

        [Fact]
        public void EmptyKey_Throws()
        {
            Action act = () => new MetadataLoader(new[] { "dbo { : value }" });

            act.Should().Throw<ArgumentException>().WithMessage("*Empty metadata key*");
        }

        [Fact]
        public void DuplicateKey_ThrowsClearError()
        {
            Action act = () => new MetadataLoader(new[] { "dbo { label: A; label: B }" });

            act.Should().Throw<ArgumentException>().WithMessage("*Duplicate metadata key 'label'*");
        }

        // ---- #4 Regex metacharacters in identifiers -------------------------

        [Fact]
        public void SelectorWithRegexMetacharacters_MatchesLiterally()
        {
            var props = new Dictionary<string, object?>();
            // 'order+items' — '+' must be a literal, not a regex quantifier.
            new MetadataLoader(new[] { "order+items { label: Orders }" })
                .ApplyDatabaseMetadata(props, "order+items");

            props.Should().ContainKey(MetadataKeys.Ui.Label);
        }

        [Fact]
        public void SelectorWithRegexMetacharacters_DoesNotOverMatch()
        {
            var props = new Dictionary<string, object?>();
            // Literal '+' must NOT match the regex expansion "orderitems"/"orderrritems".
            new MetadataLoader(new[] { "order+items { label: Orders }" })
                .ApplyDatabaseMetadata(props, "orderitems");

            props.Should().NotContainKey(MetadataKeys.Ui.Label);
        }

        [Fact]
        public void SelectorWithParentheses_MatchesLiterally()
        {
            var props = new Dictionary<string, object?>();
            new MetadataLoader(new[] { "data(2024) { label: Snapshot }" })
                .ApplyDatabaseMetadata(props, "data(2024)");

            props.Should().ContainKey(MetadataKeys.Ui.Label);
        }

        [Fact]
        public void WildcardStar_StillExpandsAfterEscaping()
        {
            var matched = new Dictionary<string, object?>();
            var loader = new MetadataLoader(new[] { "app_* { label: Prefixed }" });

            loader.ApplyDatabaseMetadata(matched, "app_users");
            matched.Should().ContainKey(MetadataKeys.Ui.Label);

            var unmatched = new Dictionary<string, object?>();
            loader.ApplyDatabaseMetadata(unmatched, "other_users");
            unmatched.Should().NotContainKey(MetadataKeys.Ui.Label);
        }

        // ---- Verbatim (backtick) values: complex natural-form values ---------

        [Fact]
        public void VerbatimValue_PreservesSemicolons()
        {
            // Multi-column computed-sql: the internal ';' separator must survive
            // the property-block ';' delimiter.
            var props = ApplyToSchema(
                "dbo { computed-sql: `full:String:{first} + {last}; other:String:{a} || {b}` }");

            props[MetadataKeys.Computed.Sql]
                .Should().Be("full:String:{first} + {last}; other:String:{a} || {b}");
        }

        [Fact]
        public void VerbatimValue_PreservesInteriorExactly()
        {
            // Interior whitespace and colons are kept verbatim, not trimmed/split.
            var props = ApplyToSchema("dbo { computed-sql: `  a:b:c  ` }");

            props[MetadataKeys.Computed.Sql].Should().Be("  a:b:c  ");
        }

        [Fact]
        public void VerbatimValue_CoexistsWithPlainProperties()
        {
            var props = ApplyToSchema(
                "dbo { computed-sql: `a:String:{x}; b:String:{y}`; label: Orders }");

            props[MetadataKeys.Computed.Sql].Should().Be("a:String:{x}; b:String:{y}");
            props[MetadataKeys.Ui.Label].Should().Be("Orders");
        }

        [Fact]
        public void VerbatimComputedSql_ParsesIntoMultipleColumns_EndToEnd()
        {
            // The loader-preserved multi-column value must feed the computed-sql
            // collector as two distinct definitions.
            var props = new Dictionary<string, object?>();
            new MetadataLoader(new[] { "dbo { computed-sql: `a:Float:{subtotal}; b:Float:{tax}` }" })
                .ApplyDatabaseMetadata(props, "dbo");
            var stored = (string)props[MetadataKeys.Computed.Sql]!;

            var model = DbModelTestFixture.Create()
                .WithTable("Orders", t => t.WithPrimaryKey("Id")
                    .WithColumn("subtotal", "decimal")
                    .WithColumn("tax", "decimal")
                    .WithMetadata(MetadataKeys.Computed.Sql, stored))
                .Build();
            var table = model.GetTableFromDbName("Orders");

            ComputedColumnConfigCollector.FromTable(table).Select(c => c.Name)
                .Should().BeEquivalentTo("a", "b");
        }

        [Fact]
        public void SingleCompleteVerbatimSpan_IsUnwrapped()
        {
            // The whole value is one span: opening '`' and the value's only other
            // '`' at the very end — the pair is stripped, interior kept verbatim.
            var props = ApplyToSchema("dbo { label: `Orders: pending; archived` }");

            props[MetadataKeys.Ui.Label].Should().Be("Orders: pending; archived");
        }

        [Fact]
        public void MultiSpanValue_KeepsBackticksVerbatim()
        {
            // Two spans in one value. Unwrap must NOT strip the outer pair —
            // doing so previously corrupted "`a` and `b`" into "a` and `b".
            // Backticks surviving in multi-span values is the documented behavior.
            var props = ApplyToSchema("dbo { label: `a` and `b` }");

            props[MetadataKeys.Ui.Label].Should().Be("`a` and `b`");
        }

        [Fact]
        public void MidStringVerbatimSpan_IsPreservedWithBackticks()
        {
            // A span embedded mid-string still protects its ';' from the property
            // splitter, but the value is not "wrapped", so nothing is stripped.
            var props = ApplyToSchema("dbo { note: prefix `x;y` suffix; label: L }");

            props["note"].Should().Be("prefix `x;y` suffix");
            props[MetadataKeys.Ui.Label].Should().Be("L");
        }

        [Fact]
        public void UnbalancedBacktick_Throws()
        {
            Action act = () => new MetadataLoader(new[] { "dbo { computed-sql: `a:b:c }" });

            act.Should().Throw<ArgumentException>().WithMessage("*Unbalanced backtick*");
        }

        // ---- Append semantics across rules -----------------------------------

        [Fact]
        public void ManyToMany_AppendsAcrossRules()
        {
            var props = new Dictionary<string, object?>();
            var loader = new MetadataLoader(new[]
            {
                "dbo { many-to-many: Roles:UserRoles }",
                "dbo { many-to-many: Tags:PostTags }",
            });

            loader.ApplyDatabaseMetadata(props, "dbo");

            props[MetadataKeys.Relationships.ManyToMany]
                .Should().Be("Roles:UserRoles, Tags:PostTags");
        }

        [Fact]
        public void Join_StillAppendsAcrossRules()
        {
            var props = new Dictionary<string, object?>();
            var loader = new MetadataLoader(new[]
            {
                "dbo { join: a }",
                "dbo { join: b }",
            });

            loader.ApplyDatabaseMetadata(props, "dbo");

            props[MetadataKeys.Relationships.Join].Should().Be("a, b");
        }

        [Fact]
        public void NonAppendProperty_LastRuleWins()
        {
            var props = new Dictionary<string, object?>();
            var loader = new MetadataLoader(new[]
            {
                "dbo { label: First }",
                "dbo { label: Second }",
            });

            loader.ApplyDatabaseMetadata(props, "dbo");

            props[MetadataKeys.Ui.Label].Should().Be("Second");
        }

        // ---- Key alias normalization -----------------------------------------

        [Theory]
        [InlineData("min-length", "5")]
        [InlineData("max-length", "50")]
        public void KebabLengthKeys_NormalizeToStoredKey(string alias, string value)
        {
            var props = ApplyToSchema($"dbo {{ {alias}: {value} }}", "dbo");

            var canonical = alias == "min-length"
                ? MetadataKeys.Validation.MinLength
                : MetadataKeys.Validation.MaxLength;
            props[canonical].Should().Be(value);
        }

        [Fact]
        public void NormalizeKey_LeavesUnknownKeysUnchanged()
        {
            MetadataKeys.NormalizeKey("tenant-filter").Should().Be("tenant-filter");
            MetadataKeys.NormalizeKey("min-length").Should().Be(MetadataKeys.Validation.MinLength);
        }

        // ---- Regression: existing simple rules unaffected --------------------

        [Fact]
        public void SimpleRule_StillApplies()
        {
            var props = ApplyToSchema("dbo { tenant-filter: tenant_id }");

            props[MetadataKeys.Security.TenantFilter].Should().Be("tenant_id");
        }
    }
}
