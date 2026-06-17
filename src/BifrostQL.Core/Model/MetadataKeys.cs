namespace BifrostQL.Core.Model
{
    /// <summary>
    /// Centralized constants for metadata key names used throughout BifrostQL.
    /// Using these constants prevents typos and enables easier refactoring.
    /// </summary>
    public static class MetadataKeys
    {
        /// <summary>
        /// Metadata keys for EAV (Entity-Attribute-Value) configuration.
        /// </summary>
        public static class Eav
        {
            /// <summary>The parent table name for an EAV meta table.</summary>
            public const string Parent = "eav-parent";

            /// <summary>The foreign key column linking to the parent table.</summary>
            public const string ForeignKey = "eav-fk";

            /// <summary>The column containing the attribute/key name.</summary>
            public const string Key = "eav-key";

            /// <summary>The column containing the attribute value.</summary>
            public const string Value = "eav-value";
        }

        /// <summary>
        /// Metadata keys for file storage configuration.
        /// </summary>
        public static class FileStorage
        {
            /// <summary>Enables file storage for this column (legacy alias).</summary>
            public const string Storage = "file-storage";

            /// <summary>
            /// Column-level tag marking a column as a file column. Carries an optional
            /// inline configuration blob parsed by <c>FileColumnConfig.FromMetadata</c>.
            /// </summary>
            public const string File = "file";

            /// <summary>Maximum file size in bytes.</summary>
            public const string MaxSize = "max-size";

            /// <summary>Column containing the MIME type.</summary>
            public const string ContentTypeColumn = "content-type-column";

            /// <summary>Column containing the original filename.</summary>
            public const string FileNameColumn = "file-name-column";

            /// <summary>Accepted file types (MIME type pattern).</summary>
            public const string Accept = "accept";

            /// <summary>
            /// Table-level virtual folder columns for CMS/DAM-style file listings.
            /// Format: <c>field:JSON:local:folder=assets/{Id},depends=Id</c>.
            /// </summary>
            public const string Folder = "file-folder";
        }

        /// <summary>
        /// Metadata keys for data type and format hints.
        /// </summary>
        public static class DataType
        {
            /// <summary>Override the detected data type.</summary>
            public const string Type = "type";

            /// <summary>Data format hint (e.g., "php", "json", "xml").</summary>
            public const string Format = "format";

            /// <summary>Indicates PHP serialized data.</summary>
            public const string PhpSerialized = "php_serialized";

            /// <summary>Default value for a column.</summary>
            public const string Default = "default";

            /// <summary>Display title; also serves as a fallback for pattern-message.</summary>
            public const string Title = "title";
        }

        /// <summary>
        /// Metadata keys for storage bucket configuration.
        /// </summary>
        public static class Storage
        {
            /// <summary>
            /// Top-level metadata key carrying a storage bucket configuration blob
            /// at the column, table, or model level. Parsed by
            /// <c>StorageBucketConfig.FromMetadata</c>. Distinct from
            /// <see cref="FileStorage.Storage"/>, which is a legacy alias.
            /// </summary>
            public const string Config = "storage";

            /// <summary>Storage bucket name.</summary>
            public const string Bucket = "bucket";

            /// <summary>Storage provider type (local, s3, etc.).</summary>
            public const string Provider = "provider";

            /// <summary>Path prefix for stored files.</summary>
            public const string Prefix = "prefix";

            /// <summary>Base path for local storage.</summary>
            public const string BasePath = "basePath";
        }

        /// <summary>
        /// Metadata keys for UI and display configuration.
        /// </summary>
        public static class Ui
        {
            /// <summary>Column label for display.</summary>
            public const string Label = "label";

            /// <summary>Visibility marker for tables, columns, and schema artifacts.</summary>
            public const string Visibility = "visibility";

            /// <summary>Hide this table or column from the UI.</summary>
            public const string Hidden = "hidden";

            /// <summary>Mark as read-only.</summary>
            public const string ReadOnly = "readonly";

            /// <summary>
            /// Preferred client display format for a column's values. Consumed by the
            /// edit-db client to render values concisely in the user's locale instead
            /// of raw DB strings (e.g. SQL Server <c>datetime2</c> "2026-05-11T22:17:47.7636626").
            /// Recognized values: <c>date</c>, <c>datetime</c>, <c>time</c>,
            /// <c>relative</c> (humanized "4 hours ago", hover reveals the exact value),
            /// <c>number</c>, <c>percent</c>, <c>raw</c>. When unset the client infers
            /// date/datetime from the column type.
            /// </summary>
            public const string DisplayFormat = "display-format";
        }

        /// <summary>
        /// Metadata keys for validation configuration.
        /// </summary>
        public static class Validation
        {
            /// <summary>Minimum value for numeric types.</summary>
            public const string Min = "min";

            /// <summary>Maximum value for numeric types.</summary>
            public const string Max = "max";

            /// <summary>Step value for numeric inputs.</summary>
            public const string Step = "step";

            /// <summary>Minimum length for strings.</summary>
            public const string MinLength = "minlength";

            /// <summary>Maximum length for strings.</summary>
            public const string MaxLength = "maxlength";

            /// <summary>Regex pattern for validation.</summary>
            public const string Pattern = "pattern";

            /// <summary>Error message for pattern validation.</summary>
            public const string PatternMessage = "pattern-message";

            /// <summary>HTML5 input type override.</summary>
            public const string InputType = "input-type";

            /// <summary>Whether the field is required.</summary>
            public const string Required = "required";

            /// <summary>Enables server-side enforcement of validation metadata.</summary>
            public const string Server = "server-validation";

            /// <summary>Comma-separated server validation provider names.</summary>
            public const string Plugin = "validation-plugin";
        }

        /// <summary>
        /// Metadata keys for virtual computed GraphQL columns.
        /// </summary>
        public static class Computed
        {
            /// <summary>
            /// Table-level SQL computed columns. Format:
            /// <c>name:GraphQlType:{column} + {other}; other:String:{first} || ' ' || {last}</c>.
            /// </summary>
            public const string Sql = "computed-sql";

            /// <summary>
            /// Table-level provider computed columns. Format:
            /// <c>name:GraphQlType:provider:depends=id,email</c>.
            /// </summary>
            public const string Provider = "computed-plugin";
        }

        /// <summary>
        /// Metadata keys for enum configuration.
        /// </summary>
        public static class Enum
        {
            /// <summary>Comma-separated list of enum values.</summary>
            public const string Values = "enum-values";

            /// <summary>Comma-separated list of display labels for enum values.</summary>
            public const string Labels = "enum-labels";

            /// <summary>Column metadata: forces the column to render as a lookup-table enum, e.g. "enum-ref: dbo.status".</summary>
            public const string Ref = "enum-ref";
        }

        /// <summary>
        /// Metadata keys for auto-population.
        /// </summary>
        public static class AutoPopulate
        {
            /// <summary>
            /// Column-level marker tagging a column for auto-population by an audit
            /// or system module. The value names the populator (e.g. "created-on",
            /// "updated-by"). When set, the field is excluded from form rendering
            /// and treated as read-only.
            /// </summary>
            public const string Marker = "populate";

            /// <summary>Auto-populate with current timestamp.</summary>
            public const string Timestamp = "timestamp";

            /// <summary>Auto-populate with current user.</summary>
            public const string User = "user";

            /// <summary>Auto-populate with UUID/GUID.</summary>
            public const string Guid = "guid";
        }

        /// <summary>
        /// Metadata keys for audit-column population by <c>AuditMutationTransformer</c>.
        /// </summary>
        public static class Audit
        {
            /// <summary>Model-level metadata key naming the audit table.</summary>
            public const string Table = "audit-table";

            /// <summary>Legacy model-level metadata key naming the audit user claim.</summary>
            public const string LegacyUserKey = "audit-user-key";

            /// <summary>
            /// Model-level metadata key naming the user-context claim used to
            /// populate created-by / updated-by / deleted-by columns.
            /// </summary>
            public const string UserKey = "user-audit-key";
        }

        /// <summary>
        /// Metadata keys used by the tenancy and automatic-filter modules.
        /// </summary>
        public static class Security
        {
            /// <summary>Table-level column used for tenant isolation.</summary>
            public const string TenantFilter = "tenant-filter";

            /// <summary>Model-level user-context key for resolving tenant IDs.</summary>
            public const string TenantContextKey = "tenant-context-key";

            /// <summary>Table-level mappings from context claims to filter columns.</summary>
            public const string AutoFilter = "auto-filter";

            /// <summary>Model-level role name that bypasses auto filters.</summary>
            public const string AutoFilterBypassRole = "auto-filter-bypass-role";
        }

        /// <summary>
        /// Metadata keys for the server-side authorization policy engine.
        /// Configured at the table level, mirroring the tenant-filter convention:
        ///   "dbo.orders { policy-actions: read,update }"
        ///   "dbo.employees { policy-read-deny: ssn,salary }"
        /// </summary>
        public static class Policy
        {
            /// <summary>
            /// Table-level comma-separated list of permitted actions
            /// (read/create/update/delete). Unrecognized tokens are ignored.
            /// </summary>
            public const string Actions = "policy-actions";

            /// <summary>
            /// Table-level comma-separated list of columns that may not be read.
            /// </summary>
            public const string ReadDeny = "policy-read-deny";

            /// <summary>
            /// Optional table-level comma-separated list of role names the
            /// <see cref="ReadDeny"/> column list applies to. When present, the
            /// read-deny columns are blocked only for a caller holding one of
            /// these roles; every other non-admin caller may read them. When
            /// absent, the read-deny columns are blocked for every non-admin
            /// caller (the original unconditional behavior). Mirrors
            /// <see cref="RowScopeRoles"/>: it role-qualifies a restriction so a
            /// finance field can be hidden from officer/member while remaining
            /// readable by finance_manager.
            /// </summary>
            public const string ReadDenyRoles = "policy-read-deny-roles";

            /// <summary>
            /// Table-level comma-separated list of columns that may not be written.
            /// </summary>
            public const string WriteDeny = "policy-write-deny";

            /// <summary>
            /// Table-level row-scope policy expression, stored verbatim. Its
            /// compilation into a query filter is handled by a later sub-task.
            /// </summary>
            public const string RowScope = "policy-row-scope";

            /// <summary>
            /// Optional table-level comma-separated list of role names the
            /// <see cref="RowScope"/> expression applies to. When present, the
            /// row-scope filter narrows only callers holding one of these roles;
            /// every other non-admin caller is left unscoped (still subject to
            /// the tenant filter and the action grants). When absent, the
            /// row-scope filter narrows every non-admin caller.
            /// </summary>
            public const string RowScopeRoles = "policy-row-scope-roles";

            /// <summary>Default role name that bypasses all policy checks.</summary>
            public const string DefaultAdminRole = "admin";
        }

        /// <summary>
        /// Metadata keys for table-level state-machine configuration.
        /// </summary>
        public static class StateMachine
        {
            /// <summary>Table-level column that stores the entity state.</summary>
            public const string StateColumn = "state-column";

            /// <summary>Initial state assigned by later mutation pipeline integration.</summary>
            public const string InitialState = "initial-state";

            /// <summary>Comma-separated list of valid state names.</summary>
            public const string States = "states";

            /// <summary>
            /// Semicolon- or pipe-separated transition list. Format:
            /// <c>from-&gt;to[role1,role2]@event</c>. Roles and event are optional.
            /// </summary>
            public const string Transitions = "transitions";
        }

        /// <summary>
        /// Metadata keys for soft-delete filtering and mutation rewriting.
        /// </summary>
        public static class SoftDelete
        {
            /// <summary>Table-level column set when records are soft deleted.</summary>
            public const string Column = "soft-delete";

            /// <summary>Optional table-level column set to the deleting user ID.</summary>
            public const string DeletedBy = "soft-delete-by";

            /// <summary>
            /// Optional table-level role required to use the <c>_hardDelete</c>
            /// mutation argument. When unset, hard delete is open to all callers.
            /// </summary>
            public const string HardDeleteRole = "soft-delete-hard-role";

            /// <summary>Legacy model-level soft-delete type setting.</summary>
            public const string LegacyType = "soft-delete-type";

            /// <summary>Legacy model-level soft-delete column setting.</summary>
            public const string LegacyColumn = "soft-delete-column";

            /// <summary>Table-level delete behavior selector.</summary>
            public const string DeleteType = "delete-type";
        }

        /// <summary>
        /// Metadata keys for explicit relationship declarations.
        /// </summary>
        public static class Relationships
        {
            /// <summary>Metadata key for explicit join declarations.</summary>
            public const string Join = "join";

            /// <summary>
            /// Table-level many-to-many declaration. Format:
            /// <c>"TargetTable:JunctionTable[, TargetTable:JunctionTable...]"</c>.
            /// </summary>
            public const string ManyToMany = "many-to-many";

            /// <summary>
            /// Model-level toggle for emitting dynamic <c>_join</c> / <c>_single</c>
            /// containers in the GraphQL schema. Defaults to true.
            /// </summary>
            public const string DynamicJoins = "dynamic-joins";

            /// <summary>Model-level toggle for automatic join discovery.</summary>
            public const string AutoJoin = "auto-join";

            /// <summary>Model-level toggle for foreign-key join discovery.</summary>
            public const string ForeignJoins = "foreign-joins";

            /// <summary>
            /// Table-level discriminator column for a polymorphic child table
            /// (e.g. <c>entity_type</c> on a shared <c>notes</c> table).
            /// </summary>
            public const string PolymorphicTypeCol = "polymorphic-type-column";

            /// <summary>
            /// Table-level id column for a polymorphic child table — holds the
            /// referenced parent's primary-key value (e.g. <c>entity_id</c>).
            /// </summary>
            public const string PolymorphicIdCol = "polymorphic-id-column";

            /// <summary>
            /// Table-level polymorphic parent map. Format:
            /// <c>"typeValue=parentTableDbName[, typeValue=parentTableDbName...]"</c>
            /// where <c>typeValue</c> is the literal stored in the discriminator
            /// column and <c>parentTableDbName</c> is the referenced table.
            /// Uses <c>=</c> and <c>,</c> only (the metadata parser splits values
            /// on <c>:</c>, so colons are not allowed in the value).
            /// </summary>
            public const string PolymorphicMap = "polymorphic-map";
        }

        /// <summary>
        /// Metadata keys for model-level query/schema behavior.
        /// </summary>
        public static class Model
        {
            /// <summary>Default per-table limit.</summary>
            public const string DefaultLimit = "default-limit";

            /// <summary>Model-level toggle for generic table query fields.</summary>
            public const string EnableGenericTable = "enable-generic-table";

            /// <summary>Legacy model-level toggle for raw SQL schema exposure.</summary>
            public const string EnableRawSql = "enable-raw-sql";

            /// <summary>Model-level toggle for de-pluralizing GraphQL names.</summary>
            public const string DePluralize = "de-pluralize";
        }

        /// <summary>
        /// Metadata keys for batch mutation configuration.
        /// </summary>
        public static class Batch
        {
            /// <summary>Per-table override for the maximum batch size.</summary>
            public const string MaxSize = "batch-max-size";
        }

        /// <summary>
        /// Metadata keys for the raw SQL query feature.
        /// </summary>
        public static class RawSql
        {
            /// <summary>
            /// Model-level toggle that enables the <c>_rawQuery</c> field. The
            /// value must be <c>"enabled"</c> for raw SQL queries to be exposed.
            /// </summary>
            public const string Enabled = "raw-sql";

            /// <summary>Model-level role required for raw SQL execution.</summary>
            public const string Role = "raw-sql-role";

            /// <summary>Model-level raw SQL timeout in seconds.</summary>
            public const string Timeout = "raw-sql-timeout";

            /// <summary>Model-level maximum rows returned by raw SQL.</summary>
            public const string MaxRows = "raw-sql-max-rows";
        }

        /// <summary>
        /// Metadata keys for schema-prefix and schema-field presentation.
        /// </summary>
        public static class Schema
        {
            public const string Prefix = "schema-prefix";
            public const string PrefixDefault = "schema-prefix-default";
            public const string PrefixFormat = "schema-prefix-format";
            public const string Display = "schema-display";
            public const string Default = "schema-default";
            public const string Excluded = "schema-excluded";
            public const string Permissions = "schema-permissions";
        }

        /// <summary>
        /// Metadata keys for stored procedure discovery.
        /// </summary>
        public static class StoredProcedures
        {
            public const string Include = "sp-include";
            public const string Exclude = "sp-exclude";
        }

        /// <summary>
        /// Default user-context key names produced by the normalized identity
        /// contract (<c>AppIdentity</c> / <c>IdentityContextMapper</c>). These
        /// mirror the defaults read by the tenancy, auto-filter, and audit
        /// modules so a mapped identity satisfies them without extra metadata.
        ///
        /// The canonical claim set projected into the user context is:
        /// <c>tenant_id</c>, <c>tenant_ids</c>, <c>user_id</c>, <c>roles</c>,
        /// <c>permissions</c>, and the audit user key (default <c>id</c>).
        /// </summary>
        public static class Auth
        {
            /// <summary>
            /// Default user-context key carrying the tenant identifier. Matches
            /// <see cref="Security.TenantContextKey"/>'s default (<c>tenant_id</c>).
            /// </summary>
            public const string DefaultTenantContextKey = "tenant_id";

            /// <summary>
            /// Default user-context key carrying every organization/tenant
            /// identifier the user belongs to (the plural projection of
            /// <c>AppIdentity.OrgIds</c>). Written as
            /// <c>IReadOnlyList&lt;string&gt;</c>.
            /// </summary>
            public const string DefaultTenantIdsContextKey = "tenant_ids";

            /// <summary>
            /// Default user-context key carrying the explicit user identifier
            /// (the canonical <c>user_id</c> claim). Distinct from the audit
            /// user key, which is configurable and defaults to <c>id</c>.
            /// </summary>
            public const string DefaultUserIdContextKey = "user_id";

            /// <summary>
            /// Default user-context key carrying the user's roles. Matches the
            /// key read by the auto-filter module (<c>roles</c>).
            /// </summary>
            public const string DefaultRolesContextKey = "roles";

            /// <summary>
            /// Default user-context key carrying the user's permissions
            /// (the plural projection of <c>AppIdentity.Permissions</c>).
            /// Written as <c>IReadOnlyList&lt;string&gt;</c>.
            /// </summary>
            public const string DefaultPermissionsContextKey = "permissions";

            /// <summary>
            /// Default user-context key carrying the audit user identifier.
            /// Matches the default <see cref="Audit.UserKey"/> value (<c>id</c>).
            /// </summary>
            public const string DefaultUserAuditKey = "id";

            /// <summary>
            /// Provider-claim key carrying the household identifier resolved from
            /// the authenticated user's member row. Surfaced into the user context
            /// verbatim by <see cref="BifrostQL.Core.Auth.IdentityContextMapper"/>
            /// so the households policy row-scope (<c>household_id = {household_id}</c>)
            /// resolves for the caller.
            /// </summary>
            public const string HouseholdClaimKey = "household_id";
        }

        /// <summary>
        /// Metadata keys for application-schema detection.
        /// </summary>
        public static class AppSchema
        {
            public const string AutoDetect = "auto-detect-app";
            public const string App = "app-schema";
            public const string Detected = "detected-app";
            public const string PrefixGroups = "prefix-groups";
        }
    }
}
