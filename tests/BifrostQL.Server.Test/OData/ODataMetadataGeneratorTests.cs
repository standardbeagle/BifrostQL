using System.Xml.Linq;
using BifrostQL.Core.Model;
using BifrostQL.Server.OData;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace BifrostQL.Server.Test.OData
{
    /// <summary>
    /// OData slice-2 metadata/service-document generation, driven against a REAL dialect-mapped
    /// SQLite model. Proves the generators emit schema-derived, EDM-typed metadata with correct
    /// keys (including composite), nullability, and navigation — and that the SAME authoritative
    /// policy gate the query path uses filters tables, columns, and navigation endpoints by
    /// identity (fail-closed). Unsupported relationship shapes (many-to-many through a hidden
    /// junction) are omitted deterministically rather than reduced to a single-column guess.
    /// </summary>
    public sealed class ODataMetadataGeneratorTests
    {
        private static readonly XNamespace Edm = "http://docs.oasis-open.org/odata/ns/edm";
        private static readonly XNamespace Edmx = "http://docs.oasis-open.org/odata/ns/edmx";

        private static readonly string[] Seed =
        {
            "CREATE TABLE Customers (id INTEGER PRIMARY KEY, name TEXT NOT NULL);",
            "CREATE TABLE Orders (id INTEGER PRIMARY KEY, customer_id INTEGER NOT NULL REFERENCES Customers(id), secret TEXT, total REAL, created_at DATETIME);",
            "CREATE TABLE OrderLines (order_id INTEGER NOT NULL REFERENCES Orders(id), line_no INTEGER NOT NULL, qty INTEGER NOT NULL, PRIMARY KEY (order_id, line_no));",
            "CREATE TABLE Tags (id INTEGER PRIMARY KEY, label TEXT NOT NULL);",
            "CREATE TABLE OrderTags (order_id INTEGER NOT NULL REFERENCES Orders(id), tag_id INTEGER NOT NULL REFERENCES Tags(id), PRIMARY KEY (order_id, tag_id));",
            "CREATE TABLE AuditLog (id INTEGER PRIMARY KEY, message TEXT NOT NULL);",
        };

        private static readonly string[] Metadata =
        {
            // Customers is create-only: a non-admin cannot READ it (nor navigate to it).
            "main.Customers { policy-actions: create }",
            // Orders is readable by everyone, but the secret column is read-denied to non-admins.
            "main.Orders { policy-actions: read }",
            "main.Orders { policy-read-deny: secret }",
            // AuditLog is create-only: readable by admin only.
            "main.AuditLog { policy-actions: create }",
        };

        private static Dictionary<string, object?> Ctx(string userId, params string[] roles) => new()
        {
            [MetadataKeys.Auth.DefaultUserIdContextKey] = userId,
            [MetadataKeys.Auth.DefaultRolesContextKey] = roles,
        };

        private static ODataEntity Entity(IReadOnlyList<ODataEntity> entities, string dbName)
            => entities.Single(e => e.Table.DbName == dbName);

        [Fact]
        public async Task ServiceDocument_and_metadata_list_only_tables_the_identity_may_read()
        {
            await using var harness = await ODataRealDbHarness.StartAsync("visible", Metadata, Seed);
            var model = await harness.ModelAsync();

            var member = ODataModelVisibility.Project(model, Ctx("u-member", "member"));
            var admin = ODataModelVisibility.Project(model, Ctx("u-admin", "admin"));

            // Member: readable tables only. Customers (create-only) and AuditLog (create-only) absent.
            var memberNames = member.Select(e => e.Table.DbName).ToList();
            memberNames.Should().Contain(new[] { "Orders", "OrderLines", "Tags" });
            memberNames.Should().NotContain("Customers");
            memberNames.Should().NotContain("AuditLog");

            // Admin additionally sees the create-only tables.
            var adminNames = admin.Select(e => e.Table.DbName).ToList();
            adminNames.Should().Contain(new[] { "Orders", "OrderLines", "Tags", "Customers", "AuditLog" });

            // The service document mirrors the visible-set exactly (one EntitySet entry per entity).
            var serviceDoc = ODataDocumentWriter.WriteServiceDocument(member, "http://host/odata");
            var doc = System.Text.Json.JsonDocument.Parse(serviceDoc);
            doc.RootElement.GetProperty("@odata.context").GetString().Should().Be("http://host/odata/$metadata");
            var setNames = doc.RootElement.GetProperty("value").EnumerateArray()
                .Select(v => v.GetProperty("name").GetString()).ToList();
            setNames.Should().BeEquivalentTo(member.Select(e => e.Table.GraphQlName));
            doc.RootElement.GetProperty("value").EnumerateArray()
                .Should().OnlyContain(v => v.GetProperty("kind").GetString() == "EntitySet");
        }

        [Fact]
        public async Task Columns_are_edm_typed_with_nullability_and_read_denied_columns_are_absent()
        {
            await using var harness = await ODataRealDbHarness.StartAsync("columns", Metadata, Seed);
            var model = await harness.ModelAsync();

            var memberOrders = Entity(ODataModelVisibility.Project(model, Ctx("u-member", "member")), "Orders");
            var adminOrders = Entity(ODataModelVisibility.Project(model, Ctx("u-admin", "admin")), "Orders");

            var memberCols = memberOrders.Columns.Select(c => c.DbName).ToList();
            memberCols.Should().Contain(new[] { "id", "customer_id", "total", "created_at" });
            memberCols.Should().NotContain("secret"); // read-denied to non-admins

            // Admin bypasses the column read-deny.
            adminOrders.Columns.Select(c => c.DbName).Should().Contain("secret");

            // Deterministic EDM primitive mapping via the model's own dialect type mapper.
            string EdmType(string col) => ODataEdmTypes.ForColumn(
                memberOrders.Columns.Single(c => c.DbName == col), model.TypeMapper);
            EdmType("id").Should().Be("Edm.Int32");            // INTEGER
            EdmType("total").Should().Be("Edm.Double");         // REAL
            EdmType("created_at").Should().Be("Edm.DateTimeOffset"); // DATETIME (OData v4 instant)

            // Nullability is projected from the column, not guessed.
            memberOrders.Columns.Single(c => c.DbName == "id").IsNullable.Should().BeFalse();
            memberOrders.Columns.Single(c => c.DbName == "total").IsNullable.Should().BeTrue();

            // The EDMX renders the same facts: <Property> with Type + Nullable, and secret absent.
            var xml = ODataDocumentWriter.WriteMetadata(
                ODataModelVisibility.Project(model, Ctx("u-member", "member")), model.TypeMapper);
            var entityType = MetadataEntityType(xml, memberOrders.Table.GraphQlName);
            var idProp = entityType.Elements(Edm + "Property")
                .Single(p => (string?)p.Attribute("Name") == model.GetTableFromDbName("Orders")
                    .Columns.Single(c => c.DbName == "id").GraphQlName);
            ((string?)idProp.Attribute("Type")).Should().Be("Edm.Int32");
            ((string?)idProp.Attribute("Nullable")).Should().Be("false");
            entityType.Elements(Edm + "Property").Select(p => (string?)p.Attribute("Name"))
                .Should().NotContain(model.GetTableFromDbName("Orders")
                    .Columns.Single(c => c.DbName == "secret").GraphQlName);
        }

        [Fact]
        public async Task Composite_primary_key_is_represented_in_full_in_the_edmx_key()
        {
            await using var harness = await ODataRealDbHarness.StartAsync("compositekey", Metadata, Seed);
            var model = await harness.ModelAsync();

            var admin = ODataModelVisibility.Project(model, Ctx("u-admin", "admin"));
            var orderLines = Entity(admin, "OrderLines");

            // The projection carries BOTH key columns, in order — never a first-column guess.
            orderLines.KeyColumns.Select(c => c.DbName).Should().Equal("order_id", "line_no");

            // The EDMX <Key> lists a PropertyRef for each — a composite key, not a single column.
            var xml = ODataDocumentWriter.WriteMetadata(admin, model.TypeMapper);
            var entityType = MetadataEntityType(xml, orderLines.Table.GraphQlName);
            var keyRefs = entityType.Element(Edm + "Key")!.Elements(Edm + "PropertyRef")
                .Select(r => (string?)r.Attribute("Name")).ToList();
            keyRefs.Should().HaveCount(2);
            keyRefs.Should().Equal(
                orderLines.Table.Columns.Single(c => c.DbName == "order_id").GraphQlName,
                orderLines.Table.Columns.Single(c => c.DbName == "line_no").GraphQlName);
        }

        [Fact]
        public async Task Navigations_are_identity_filtered_and_many_to_many_is_omitted()
        {
            await using var harness = await ODataRealDbHarness.StartAsync("navigation", Metadata, Seed);
            var model = await harness.ModelAsync();

            var customersName = model.GetTableFromDbName("Customers").GraphQlName;
            var orderLinesName = model.GetTableFromDbName("OrderLines").GraphQlName;
            var tagsName = model.GetTableFromDbName("Tags").GraphQlName;

            var adminOrders = Entity(ODataModelVisibility.Project(model, Ctx("u-admin", "admin")), "Orders");
            var memberOrders = Entity(ODataModelVisibility.Project(model, Ctx("u-member", "member")), "Orders");

            // Admin sees the single (many-to-one) nav to Customers and the collection nav to OrderLines.
            adminOrders.Navigations.Should().Contain(n => n.TargetEntity == customersName && !n.IsCollection);
            adminOrders.Navigations.Should().Contain(n => n.TargetEntity == orderLinesName && n.IsCollection);

            // The many-to-many Orders<->Tags (through the hidden OrderTags junction) is an
            // unsupported shape and is OMITTED — never a silent single-column guess.
            adminOrders.Navigations.Should().NotContain(n => n.TargetEntity == tagsName);

            // Member cannot read Customers, so the navigation endpoint to it is absent — while the
            // navigation to a table it CAN read (OrderLines) is still present.
            memberOrders.Navigations.Should().NotContain(n => n.TargetEntity == customersName);
            memberOrders.Navigations.Should().Contain(n => n.TargetEntity == orderLinesName && n.IsCollection);

            // The EDMX renders the same nav set for admin (collection type wrapped correctly).
            var xml = ODataDocumentWriter.WriteMetadata(
                ODataModelVisibility.Project(model, Ctx("u-admin", "admin")), model.TypeMapper);
            var entityType = MetadataEntityType(xml, adminOrders.Table.GraphQlName);
            var navTypes = entityType.Elements(Edm + "NavigationProperty")
                .Select(n => (string?)n.Attribute("Type")).ToList();
            navTypes.Should().Contain($"Collection({ODataDocumentWriter.SchemaNamespace}.{orderLinesName})");
            navTypes.Should().Contain($"{ODataDocumentWriter.SchemaNamespace}.{customersName}");
            navTypes.Should().NotContain(t => t != null && t.Contains(tagsName));
        }

        [Fact]
        public async Task Metadata_is_valid_edmx_with_a_container_entityset_per_visible_entity()
        {
            await using var harness = await ODataRealDbHarness.StartAsync("edmx", Metadata, Seed);
            var model = await harness.ModelAsync();

            var admin = ODataModelVisibility.Project(model, Ctx("u-admin", "admin"));
            var xml = ODataDocumentWriter.WriteMetadata(admin, model.TypeMapper);

            var root = XDocument.Parse(xml).Root!;
            root.Name.Should().Be(Edmx + "Edmx");
            ((string?)root.Attribute("Version")).Should().Be("4.0");

            var container = root.Descendants(Edm + "EntityContainer").Single();
            var setTypes = container.Elements(Edm + "EntitySet")
                .Select(s => (string?)s.Attribute("EntityType")).ToList();
            setTypes.Should().BeEquivalentTo(
                admin.Select(e => $"{ODataDocumentWriter.SchemaNamespace}.{e.Table.GraphQlName}"));
        }

        // ---- fail-closed on unparseable policy (mocked model, mirrors pgwire prior art) --------

        [Fact]
        public void Unparseable_policy_excludes_the_table_for_everyone_including_admin()
        {
            // A table whose policy-actions cannot be parsed must be excluded fail-closed — the
            // collector throws before the evaluator runs, so even admin never sees it.
            var ok = Table("ok", policyActions: null);
            var broken = Table("broken", policyActions: "frobnicate");
            var model = Substitute.For<IDbModel>();
            model.Tables.Returns(new[] { ok, broken });

            var adminEntities = ODataModelVisibility.Project(model, Ctx("u-admin", "admin"));

            adminEntities.Select(e => e.Table.DbName).Should().Contain("ok");
            adminEntities.Select(e => e.Table.DbName).Should().NotContain("broken");
        }

        private static IDbTable Table(string name, string? policyActions)
        {
            var table = Substitute.For<IDbTable>();
            table.DbName.Returns(name);
            table.GraphQlName.Returns(name);
            table.TableSchema.Returns("dbo");
            table.Columns.Returns(new[] { new ColumnDto { ColumnName = "id", GraphQlName = "id", DataType = "int", OrdinalPosition = 1, IsPrimaryKey = true } });
            table.KeyColumns.Returns(new[] { new ColumnDto { ColumnName = "id", GraphQlName = "id", DataType = "int", OrdinalPosition = 1, IsPrimaryKey = true } });
            table.SingleLinks.Returns(new Dictionary<string, TableLinkDto>());
            table.MultiLinks.Returns(new Dictionary<string, TableLinkDto>());
            table.GetMetadataValue(MetadataKeys.Policy.Actions).Returns(policyActions);
            return table;
        }

        private static XElement MetadataEntityType(string xml, string entityTypeName)
            => XDocument.Parse(xml).Root!
                .Descendants(Edm + "EntityType")
                .Single(e => (string?)e.Attribute("Name") == entityTypeName);
    }
}
