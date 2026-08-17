using System.Collections.Generic;
using System.Linq;
using BifrostQL.Core.Model;
using NSubstitute;

namespace BifrostQL.Server.Test.Ldap
{
    /// <summary>
    /// Builds small LDAP-mapped <see cref="IDbModel"/> fixtures from real <see cref="DbTable"/>
    /// instances, so the directory index and the filter compiler run against the SAME metadata
    /// parse (<c>LdapMappingConfig.FromTable</c>) production uses. Only the model container is
    /// substituted — its tables, columns, metadata, and links are real.
    /// </summary>
    internal sealed class LdapModelBuilder
    {
        private readonly List<DbTable> _tables = new();
        private readonly Dictionary<string, object?> _modelMetadata = new();

        public static LdapModelBuilder Create(string baseDn = "dc=example,dc=com")
        {
            var builder = new LdapModelBuilder();
            if (baseDn.Length > 0)
                builder._modelMetadata[MetadataKeys.Ldap.BaseDn] = baseDn;
            return builder;
        }

        public LdapModelBuilder WithModelMetadata(string key, object? value)
        {
            _modelMetadata[key] = value;
            return this;
        }

        public LdapModelBuilder WithTable(string name, Action<LdapTableBuilder> configure)
        {
            var builder = new LdapTableBuilder(name);
            configure(builder);
            _tables.Add(builder.Build());
            return this;
        }

        /// <summary>The canonical people table: uid/cn/mail entries under <c>ou=people</c>.</summary>
        public LdapModelBuilder WithPeople(
            string dnTemplate = "uid={username},ou=people",
            string attributes = "uid=username,cn=full_name,mail=email")
            => WithTable("users", t => t
                .WithColumn("id", "int", isPrimaryKey: true)
                .WithColumn("username", "nvarchar")
                .WithColumn("full_name", "nvarchar")
                .WithColumn("email", "nvarchar")
                .WithColumn("password_hash", "nvarchar")
                .WithColumn("uid_number", "int")
                .WithMetadata(MetadataKeys.Ldap.ObjectClass, "inetOrgPerson")
                .WithMetadata(MetadataKeys.Ldap.DnTemplate, dnTemplate)
                .WithMetadata(MetadataKeys.Ldap.Attributes, attributes)
                .WithMetadata(MetadataKeys.Ldap.Credential, "password_hash"));

        /// <summary>The canonical groups table under <c>ou=groups</c>.</summary>
        public LdapModelBuilder WithGroups(string? memberRelationship = null)
            => WithTable("groups", t =>
            {
                t.WithColumn("id", "int", isPrimaryKey: true)
                    .WithColumn("name", "nvarchar")
                    .WithColumn("description", "nvarchar")
                    .WithMetadata(MetadataKeys.Ldap.ObjectClass, "groupOfNames")
                    .WithMetadata(MetadataKeys.Ldap.DnTemplate, "cn={name},ou=groups")
                    .WithMetadata(MetadataKeys.Ldap.Attributes, "cn=name,description=description");
                if (memberRelationship is not null)
                    t.WithMetadata(MetadataKeys.Ldap.Member, memberRelationship);
            });

        public DbTable Table(string name) =>
            _tables.First(t => string.Equals(t.DbName, name, StringComparison.OrdinalIgnoreCase));

        /// <summary>Links <c>groups</c> to <c>users</c> through a junction, as a many-to-many bridge.</summary>
        public LdapModelBuilder WithMembershipBridge(
            string linkName = "members",
            string sourceTable = "groups", string targetTable = "users",
            string junctionTable = "user_groups")
        {
            WithTable(junctionTable, t => t
                .WithColumn("id", "int", isPrimaryKey: true)
                .WithColumn("group_id", "int")
                .WithColumn("user_id", "int"));

            var source = Table(sourceTable);
            var target = Table(targetTable);
            var junction = Table(junctionTable);

            source.ManyToManyLinks[linkName] = new ManyToManyLink
            {
                Name = linkName,
                SourceTable = source,
                JunctionTable = junction,
                TargetTable = target,
                SourceColumn = source.ColumnLookup["id"],
                JunctionSourceColumn = junction.ColumnLookup["group_id"],
                JunctionTargetColumn = junction.ColumnLookup["user_id"],
                TargetColumn = target.ColumnLookup["id"],
            };
            return this;
        }

        public IDbModel Build()
        {
            var model = Substitute.For<IDbModel>();
            model.Tables.Returns(_tables.Cast<IDbTable>().ToList());
            model.Metadata.Returns(_modelMetadata);
            model.GetMetadataValue(Arg.Any<string>()).Returns(call =>
                _modelMetadata.TryGetValue(call.Arg<string>(), out var v) ? v?.ToString() : null);
            model.GetTableFromDbName(Arg.Any<string>()).Returns(call =>
                _tables.FirstOrDefault(t =>
                    string.Equals(t.DbName, call.Arg<string>(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"no table '{call.Arg<string>()}'"));
            return model;
        }
    }

    internal sealed class LdapTableBuilder
    {
        private readonly string _name;
        private readonly Dictionary<string, ColumnDto> _columns =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, object?> _metadata = new();
        private string _schema = "dbo";

        public LdapTableBuilder(string name) => _name = name;

        public LdapTableBuilder WithSchema(string schema)
        {
            _schema = schema;
            return this;
        }

        public LdapTableBuilder WithColumn(
            string name, string dataType, bool isPrimaryKey = false, bool isNullable = true)
        {
            _columns[name] = new ColumnDto
            {
                TableSchema = _schema,
                TableName = _name,
                ColumnName = name,
                GraphQlName = name,
                NormalizedName = name,
                DataType = dataType,
                IsPrimaryKey = isPrimaryKey,
                IsNullable = isNullable,
                OrdinalPosition = _columns.Count + 1,
            };
            return this;
        }

        public LdapTableBuilder WithMetadata(string key, object? value)
        {
            _metadata[key] = value;
            return this;
        }

        public DbTable Build()
        {
            var table = new DbTable
            {
                DbName = _name,
                GraphQlName = _name,
                NormalizedName = _name,
                TableSchema = _schema,
                ColumnLookup = _columns,
                GraphQlLookup = _columns.Values.ToDictionary(c => c.GraphQlName, c => c),
            };
            foreach (var (key, value) in _metadata)
                table.Metadata[key] = value;
            return table;
        }
    }
}
