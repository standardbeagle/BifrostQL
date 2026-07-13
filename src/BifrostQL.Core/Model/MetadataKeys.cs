namespace BifrostQL.Core.Model
{
    /// <summary>
    /// Centralized constants for metadata key names used throughout BifrostQL.
    /// Using these constants prevents typos and enables easier refactoring.
    /// </summary>
    public static class MetadataKeys
    {
        /// <summary>
        /// Normalizes a metadata property key written in the preferred kebab-case
        /// spelling onto the canonical stored key. Lets configuration authors use
        /// a consistent kebab-case style for keys whose stored form predates that
        /// convention (e.g. <c>min-length</c> → <c>minlength</c>) without changing
        /// the constants that consumers read. Unknown keys pass through unchanged.
        /// </summary>
        public static string NormalizeKey(string key) => key switch
        {
            "min-length" => Validation.MinLength,
            "max-length" => Validation.MaxLength,
            _ => key,
        };

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

            /// <summary>
            /// Opt-out switch for server-side validation enforcement. Validation runs
            /// by default on Insert/Update; set this to off/false/disabled/none/no/0
            /// (at the table or column level) to turn it off. Legacy enable values
            /// (true/enabled/server) are honoured as no-ops since validation is on.
            /// </summary>
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

            // --- Recognized values of the <see cref="Marker"/> ("populate") key,
            // consumed by AuditMutationTransformer. A populate value outside this
            // set (e.g. "created_on" with an underscore) silently never stamps, so
            // ModelConfigValidator rejects unknown values using this set.

            /// <summary>Stamp the server UTC timestamp on INSERT only.</summary>
            public const string CreatedOn = "created-on";

            /// <summary>Stamp the audit user on INSERT only.</summary>
            public const string CreatedBy = "created-by";

            /// <summary>Stamp the server UTC timestamp on INSERT and UPDATE.</summary>
            public const string UpdatedOn = "updated-on";

            /// <summary>Stamp the audit user on INSERT and UPDATE.</summary>
            public const string UpdatedBy = "updated-by";

            /// <summary>Stamp the server UTC timestamp on DELETE only.</summary>
            public const string DeletedOn = "deleted-on";

            /// <summary>Stamp the audit user on DELETE only.</summary>
            public const string DeletedBy = "deleted-by";

            /// <summary>
            /// The complete set of recognized audit populator values for the
            /// <see cref="Marker"/> key. Used by ModelConfigValidator to fail fast on
            /// a typo'd populator value (which would otherwise silently never stamp).
            /// </summary>
            public static readonly IReadOnlySet<string> KnownPopulators =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    CreatedOn, CreatedBy, UpdatedOn, UpdatedBy, DeletedOn, DeletedBy,
                };
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

            /// <summary>
            /// Table-level delete behavior selector. <b>Currently inert:</b> no
            /// reader consumes this key anywhere in the codebase. Soft-delete
            /// behavior today is driven solely by the presence/absence of
            /// <see cref="Column"/> (the mutation pipeline rewrites a delete to a
            /// soft-delete update whenever <see cref="Column"/> is configured;
            /// there is no other selectable behavior). Kept and allow-listed so
            /// existing configs that harmlessly set it are not broken. Wiring it
            /// to actually gate delete-vs-soft-delete selection is a documented
            /// design follow-up, not implemented here.
            /// </summary>
            public const string DeleteType = "delete-type";
        }

        /// <summary>
        /// Metadata keys for optimistic-concurrency (lost-update prevention).
        /// </summary>
        public static class Concurrency
        {
            /// <summary>
            /// Table-level version-token column. An update must carry the token value it
            /// read; the mutation pipeline ANDs <c>token = @clientVersion</c> into the
            /// UPDATE WHERE and bumps the token in SET, raising a CONFLICT when the
            /// guarded update affects zero rows (a concurrent write moved the token).
            /// </summary>
            public const string Token = "concurrency-token";
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

            /// <summary>
            /// Model-level role required to execute stored procedures through the
            /// stored-procedure resolver. Mirrors <see cref="RawSql.Role"/>: when
            /// unset, execution is open to all callers (no gate).
            /// </summary>
            public const string Role = "stored-procedure-role";
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
            public const string DetectionConfidence = "detection-confidence";
            public const string PrefixGroups = "prefix-groups";
        }

        /// <summary>
        /// Metadata keys for field-level encryption and role-based masking. Configured
        /// at the column level:
        ///   "dbo.customers.ssn { encrypt: aes-256-gcm; key-ref: kms:pii; mask: last4; unmask-role: compliance; blind-index: ssn_bidx }"
        /// This slice establishes the metadata contract, the key-management layer, and
        /// fail-fast validation; the encrypt-on-write transformer and decrypt/mask-on-read
        /// guard are later Crypto sub-tasks.
        /// </summary>
        public static class Crypto
        {
            /// <summary>
            /// Column-level marker enabling envelope encryption of the column's values
            /// at rest. The value is the algorithm (only <c>aes-256-gcm</c> today).
            /// Presence opts the column into encryption.
            /// </summary>
            public const string Encrypt = "encrypt";

            /// <summary>
            /// Column-level reference to the data-encryption key (DEK) used for this
            /// column, in <c>provider:id</c> form (e.g. <c>kms:pii</c>, <c>config:pii</c>).
            /// Required when <see cref="Encrypt"/> is present so multiple columns can
            /// share or separate keys deliberately.
            /// </summary>
            public const string KeyRef = "key-ref";

            /// <summary>
            /// Column-level mask mode applied to the plaintext for callers WITHOUT the
            /// <see cref="UnmaskRole"/>. One of <see cref="MaskModes"/>. Defaults to
            /// <c>redact</c> (fully hidden) when omitted.
            /// </summary>
            public const string Mask = "mask";

            /// <summary>
            /// Column-level role name that may read the decrypted plaintext. Callers
            /// without it receive the masked value. When omitted, only the default
            /// admin role sees plaintext (fail-closed).
            /// </summary>
            public const string UnmaskRole = "unmask-role";

            /// <summary>
            /// Column-level name of a sibling column that stores a deterministic
            /// blind-index (keyed HMAC) of the plaintext, so equality search remains
            /// possible on an otherwise non-deterministically encrypted column. The
            /// named column must exist on the same table.
            /// </summary>
            public const string BlindIndex = "blind-index";

            /// <summary>The only supported encryption algorithm value.</summary>
            public const string AlgorithmAes256Gcm = "aes-256-gcm";

            /// <summary>Recognized mask modes (what a non-unmask-role caller sees).</summary>
            public const string MaskRedact = "redact";
            public const string MaskLast4 = "last4";
            public const string MaskEmail = "email";

            /// <summary>Recognized <see cref="Encrypt"/> algorithm values.</summary>
            public static readonly IReadOnlySet<string> Algorithms =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { AlgorithmAes256Gcm };

            /// <summary>Recognized <see cref="Mask"/> modes.</summary>
            public static readonly IReadOnlySet<string> MaskModes =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { MaskRedact, MaskLast4, MaskEmail };

            /// <summary>Recognized <see cref="KeyRef"/> providers (the part before the colon).</summary>
            public static readonly IReadOnlySet<string> KeyRefProviders =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "kms", "config" };
        }

        /// <summary>
        /// Metadata keys for Change Data Capture / outbound domain events. A table
        /// opts in by declaring which mutations emit events; the model names the
        /// transactional outbox table the events are written to. Configured like the
        /// tenant-filter convention:
        ///   "dbo.orders { emit-events: insert,update,delete; event-sink: outbox; event-payload: changed }"
        ///   ":root { outbox-table: dbo.__outbox; webhook-secret: ... }"
        /// The runtime writer (before-commit hook) and dispatcher are later CDC
        /// sub-tasks; this slice establishes the metadata contract, the outbox column
        /// contract (<see cref="Cdc.OutboxColumns"/>), and fail-fast validation.
        /// </summary>
        public static class Cdc
        {
            /// <summary>
            /// Table-level comma-separated list of mutation operations that emit an
            /// event. Tokens are the <c>MutationType</c> names: <c>insert</c>,
            /// <c>update</c>, <c>delete</c> (case-insensitive). Presence of this key
            /// is what opts a table into event emission.
            /// </summary>
            public const string EmitEvents = "emit-events";

            /// <summary>
            /// Table-level durable event sink. The only sink in this slice is
            /// <c>outbox</c> (a transactional outbox row written in the same
            /// transaction as the data change). Defaults to <c>outbox</c> when
            /// <see cref="EmitEvents"/> is present and this key is omitted.
            /// </summary>
            public const string EventSink = "event-sink";

            /// <summary>
            /// Table-level payload mode controlling how much of the row is captured
            /// in the event: <c>full</c> (entire post-image), <c>changed</c> (only
            /// the changed columns plus keys), or <c>keys</c> (primary key only).
            /// Defaults to <c>full</c>. See <see cref="Cdc.PayloadModes"/>.
            /// </summary>
            public const string EventPayload = "event-payload";

            /// <summary>
            /// Model-level qualified name of the transactional outbox table events
            /// are written to (e.g. <c>dbo.__outbox</c>). Required once any table
            /// sets <see cref="EmitEvents"/>; the named table must exist and carry
            /// the <see cref="OutboxColumns"/> contract.
            /// </summary>
            public const string OutboxTable = "outbox-table";

            /// <summary>
            /// Model-level shared secret used to sign webhook deliveries drained
            /// from the outbox. Consumed by the dispatcher (a later CDC sub-task);
            /// allow-listed here so configs that set it up front are not rejected.
            /// </summary>
            public const string WebhookSecret = "webhook-secret";

            /// <summary>The only recognized <see cref="EventSink"/> value in this slice.</summary>
            public const string SinkOutbox = "outbox";

            /// <summary>Recognized <see cref="EventPayload"/> values.</summary>
            public const string PayloadFull = "full";
            public const string PayloadChanged = "changed";
            public const string PayloadKeys = "keys";

            /// <summary>
            /// The set of recognized <see cref="EventPayload"/> modes. Used by
            /// ModelConfigValidator to fail fast on a typo'd mode (which would
            /// otherwise silently fall back to a default and capture the wrong data).
            /// </summary>
            public static readonly IReadOnlySet<string> PayloadModes =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    PayloadFull, PayloadChanged, PayloadKeys,
                };

            /// <summary>
            /// The set of recognized <see cref="EventSink"/> values. Only
            /// <see cref="SinkOutbox"/> is durable-transactional today; webhook /
            /// queue sinks are drained FROM the outbox by the dispatcher rather than
            /// named here.
            /// </summary>
            public static readonly IReadOnlySet<string> Sinks =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    SinkOutbox,
                };

            /// <summary>
            /// The column contract every outbox table must expose. The before-commit
            /// writer (next CDC sub-task) writes these columns in the same
            /// transaction as the data change; the dispatcher drains and marks them.
            /// ModelConfigValidator verifies the configured <see cref="OutboxTable"/>
            /// carries every column so a misconfigured outbox fails at model load
            /// rather than on the first event write.
            /// <list type="bullet">
            ///   <item><c>id</c> — surrogate PK, monotonic (drain order).</item>
            ///   <item><c>aggregate</c> — qualified source table (e.g. <c>dbo.orders</c>).</item>
            ///   <item><c>op</c> — <c>insert</c>/<c>update</c>/<c>delete</c>.</item>
            ///   <item><c>payload</c> — JSON event body per the payload mode.</item>
            ///   <item><c>tenant</c> — tenant id captured from user context (nullable).</item>
            ///   <item><c>created_at</c> — write timestamp.</item>
            ///   <item><c>dispatched_at</c> — set by the dispatcher on success (nullable = undelivered).</item>
            ///   <item><c>attempts</c> — delivery attempt counter.</item>
            ///   <item><c>dead</c> — dead-letter flag once attempts exhaust.</item>
            /// </list>
            /// </summary>
            public static readonly IReadOnlyList<string> OutboxColumns = new[]
            {
                "id", "aggregate", "op", "payload", "tenant",
                "created_at", "dispatched_at", "attempts", "dead",
            };
        }

        /// <summary>
        /// Metadata keys for temporal change history — the before/after field-level
        /// trail beyond the created/updated audit columns. A table opts in by naming
        /// the operations it records; each recorded mutation writes one history row
        /// (before-image, after-image, changed columns, actor, timestamp) atomically
        /// with the change. Configured like the tenant-filter convention:
        ///   "dbo.orders { history: insert,update,delete; history-table: dbo.orders_history; history-columns: status,total }"
        ///   ":root { history-table: dbo.__history }"
        /// This slice establishes the metadata contract, the history column contract
        /// (<see cref="History.HistoryColumns"/>), and fail-fast validation; the
        /// before-image capture + diff writer and the history read surface are later
        /// History sub-tasks.
        /// </summary>
        public static class History
        {
            /// <summary>
            /// Table-level comma-separated list of mutation operations recorded into
            /// history. Tokens are the <c>MutationType</c> names — <c>insert</c>,
            /// <c>update</c>, <c>delete</c> (case-insensitive) — or the single token
            /// <see cref="AllOperations"/> meaning all three. Presence of this key is
            /// what opts a table into history.
            /// </summary>
            public const string Enabled = "history";

            /// <summary>
            /// Qualified name of the table history rows are written to. Valid at BOTH
            /// levels: model-level it is the shared default for every history-enabled
            /// table (e.g. <c>dbo.__history</c>); table-level it overrides that default
            /// with a per-table history table (e.g. <c>dbo.orders_history</c>). Either
            /// way the target carries the same <see cref="HistoryColumns"/> contract —
            /// per-table vs shared is a routing/partitioning choice, not a shape choice.
            /// A history-enabled table with neither is rejected at model load.
            /// </summary>
            public const string Table = "history-table";

            /// <summary>
            /// Table-level comma-separated allow-list of columns whose changes are
            /// recorded. Omitted means every column of the table is tracked. Narrowing
            /// keeps noisy or sensitive columns out of the trail. Every named column
            /// must exist on the table.
            /// </summary>
            public const string Columns = "history-columns";

            /// <summary>
            /// The <see cref="Enabled"/> token that expands to every operation. Spelled
            /// <c>enabled</c> so the common "just record everything" case reads as a
            /// plain opt-in switch rather than an operation list.
            /// </summary>
            public const string AllOperations = "enabled";

            /// <summary>
            /// The column names of the history contract (see <see cref="HistoryColumns"/>).
            /// The change-history writer writes these columns, ModelConfigValidator checks
            /// for them, and the read surface reads them back — one set of names for all
            /// three, so the contract cannot drift between writer and validator.
            /// </summary>
            public static class Column
            {
                /// <summary>Surrogate PK, monotonic (trail order).</summary>
                public const string Id = "id";

                /// <summary>Qualified source table (e.g. <c>dbo.orders</c>); constant on a per-table history table, discriminator on a shared one.</summary>
                public const string Entity = "entity";

                /// <summary>JSON object of the row's primary-key columns (composite-PK safe).</summary>
                public const string EntityId = "entity_id";

                /// <summary><c>insert</c>/<c>update</c>/<c>delete</c>.</summary>
                public const string Op = "op";

                /// <summary>User id from the audit user-context (nullable: unauthenticated/system writes).</summary>
                public const string Actor = "actor";

                /// <summary>Write timestamp.</summary>
                public const string ChangedAt = "changed_at";

                /// <summary>JSON pre-image of the tracked columns (null on insert).</summary>
                public const string Before = "before";

                /// <summary>JSON post-image of the tracked columns (null on delete).</summary>
                public const string After = "after";

                /// <summary>JSON array of the tracked columns that actually differed.</summary>
                public const string ChangedColumns = "changed_columns";
            }

            /// <summary>
            /// The column contract every history table must expose (per-table or shared
            /// alike). The diff writer writes these columns in the same transaction as
            /// the data change; the history read surface reads them back.
            /// ModelConfigValidator verifies the configured <see cref="Table"/> carries
            /// every column so a misconfigured history table fails at model load rather
            /// than aborting the first real write.
            ///
            /// A tracked table that declares <see cref="Security.TenantFilter"/> requires
            /// ONE ADDITIONAL column on its target, named after its own tenant column
            /// (e.g. <c>tenant_id</c>): every trail row materializes the tracked row's
            /// tenant scope value so history reads can be authorized by plain column
            /// predicates. The name is per tracked table, so it is not part of this fixed
            /// list — ModelConfigValidator enforces it conditionally, and a shared target
            /// serving tables with different tenant column names must carry all of them
            /// (nullable) or be split via per-table <see cref="Table"/> overrides.
            /// </summary>
            public static readonly IReadOnlyList<string> HistoryColumns = new[]
            {
                Column.Id, Column.Entity, Column.EntityId, Column.Op, Column.Actor,
                Column.ChangedAt, Column.Before, Column.After, Column.ChangedColumns,
            };
        }

        /// <summary>
        /// Metadata keys for the chat module — a metadata-driven chat schema published
        /// over user-supplied conversation/message tables. The tables are ordinary
        /// published tables the user owns, so tenant isolation, field encryption, and
        /// change history compose with them like with any other table. Configured like
        /// the tenant-filter convention:
        ///   "dbo.conversations { chat-conversations: enabled; chat-title: Title }"
        ///   "dbo.messages { chat-messages: enabled; chat-role: Role; chat-content: Content;
        ///                   chat-conversation-fk: ConversationId; chat-created-at: CreatedAt }"
        /// The HTTP surface over this contract is the chat SSE endpoints
        /// (BifrostQL.Server's UseBifrostChat / BifrostChatMiddleware) backed by
        /// ChatConversationStore over the intent executors.
        /// </summary>
        public static class Chat
        {
            /// <summary>
            /// Table-level marker declaring the chat conversations table. The value must
            /// be <see cref="Enabled"/>. Exactly one conversations table is allowed per
            /// model, paired with exactly one <see cref="Messages"/> table.
            /// </summary>
            public const string Conversations = "chat-conversations";

            /// <summary>
            /// Optional column on the conversations table holding the conversation title.
            /// Valid only alongside <see cref="Conversations"/>.
            /// </summary>
            public const string Title = "chat-title";

            /// <summary>
            /// Table-level marker declaring the chat messages table. The value must be
            /// <see cref="Enabled"/>. Requires the full column mapping
            /// (<see cref="Role"/>, <see cref="Content"/>, <see cref="ConversationFk"/>,
            /// <see cref="CreatedAt"/>) — a partial mapping is rejected at model load.
            /// </summary>
            public const string Messages = "chat-messages";

            /// <summary>
            /// Column on the messages table holding the message role (e.g. user/assistant).
            /// Must be string-typed.
            /// </summary>
            public const string Role = "chat-role";

            /// <summary>
            /// Column on the messages table holding the message content. Must be string-typed.
            /// </summary>
            public const string Content = "chat-content";

            /// <summary>
            /// Column on the messages table referencing the conversations table's
            /// single-column primary key. The reference must be a real relationship
            /// (a declared foreign key or an explicit <c>join</c> metadata rule).
            /// </summary>
            public const string ConversationFk = "chat-conversation-fk";

            /// <summary>
            /// Column on the messages table holding the message timestamp. Must be
            /// date/time-typed (message ordering within a conversation).
            /// </summary>
            public const string CreatedAt = "chat-created-at";

            /// <summary>
            /// The only valid value of <see cref="Conversations"/> / <see cref="Messages"/>.
            /// Spelled <c>enabled</c> so the opt-in reads as a plain switch, matching
            /// <c>raw-sql: enabled</c> and the <c>history: enabled</c> token.
            /// </summary>
            public const string Enabled = "enabled";
        }

        /// <summary>
        /// Metadata keys for chat connectors — tables the chat module exposes to the
        /// LLM as Claude tools. A table opts in by naming its connector types:
        /// <c>explore</c> (read/query), <c>media</c> (serve an image/file column), and
        /// <c>plan</c> (gated writes from an approved plan). Configured like the
        /// tenant-filter convention:
        ///   "dbo.orders { chat-connector: explore,plan; chat-plan-operations: insert,update }"
        ///   "dbo.documents { chat-connector: media; chat-media-column: Image;
        ///                    chat-media-vision: enabled; chat-media-caption: Caption }"
        /// This slice establishes the metadata contract
        /// (<c>ChatConnectorConfig</c>) and fail-fast validation; the generated tools —
        /// explore queries, media serving, plan execution — are later connector slices.
        /// </summary>
        public static class ChatConnector
        {
            /// <summary>
            /// Table-level comma-separated list of connector type tokens
            /// (<see cref="TypeExplore"/> / <see cref="TypeMedia"/> / <see cref="TypePlan"/>).
            /// Presence of this key is what opts a table into the chat-connector surface.
            /// </summary>
            public const string Marker = "chat-connector";

            /// <summary>
            /// The image/file column a <see cref="TypeMedia"/> connector serves. Required
            /// when the media token is present. The serving mode is derived from the
            /// column's type — a binary-typed column serves bytes (binary mode), a
            /// string-typed column serves URLs (URL mode) — so there is no explicit
            /// mode key to fall out of sync with the schema.
            /// </summary>
            public const string MediaColumn = "chat-media-column";

            /// <summary>
            /// Opt-in flag sending the media content to the model as vision input.
            /// The only valid value is <see cref="Chat.Enabled"/>. Valid only alongside
            /// the <see cref="TypeMedia"/> token.
            /// </summary>
            public const string MediaVision = "chat-media-vision";

            /// <summary>
            /// Optional string-typed column carrying a caption/alt-text for the media
            /// column's content. Valid only alongside the <see cref="TypeMedia"/> token.
            /// </summary>
            public const string MediaCaption = "chat-media-caption";

            /// <summary>
            /// Comma-separated allow-list of write operations a <see cref="TypePlan"/>
            /// connector may perform: <c>insert</c>, <c>update</c>, <c>delete</c>
            /// (the <c>MutationType</c> names, case-insensitive). Required when the plan
            /// token is present — a plan connector allowing nothing gates nothing.
            /// <c>delete</c> is NEVER implied; it must be listed explicitly.
            /// </summary>
            public const string PlanOperations = "chat-plan-operations";

            /// <summary>
            /// Optional free-text feeding the generated Claude tool description for any
            /// connector type. Present-but-empty is rejected: a blank description is
            /// worse than the schema-derived default.
            /// </summary>
            public const string ToolDescription = "chat-tool-description";

            /// <summary>Read/query connector: the table is exposed as an explore tool.</summary>
            public const string TypeExplore = "explore";

            /// <summary>Media connector: the table serves an image/file column.</summary>
            public const string TypeMedia = "media";

            /// <summary>Plan connector: gated writes from an approved plan.</summary>
            public const string TypePlan = "plan";

            /// <summary>
            /// The recognized <see cref="Marker"/> type tokens. Used to fail fast on a
            /// typo'd token, which would otherwise expose the wrong tool — or none —
            /// with no error.
            /// </summary>
            public static readonly IReadOnlySet<string> KnownTypes =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    TypeExplore, TypeMedia, TypePlan,
                };
        }
    }
}
