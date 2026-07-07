namespace BifrostQL.Core.Model.AppSchema;

/// <summary>
/// Configuration for an EAV (Entity-Attribute-Value) meta table.
/// Links a meta table to its parent entity table, enabling flattening
/// of key-value rows into a single JSON field on the parent.
/// </summary>
/// <remarks>
/// <see cref="TableSchema"/> is the meta table's own schema (e.g. "app" for
/// "app.wp_postmeta"). It must be threaded into the SQL built at the
/// <c>EavMetaProvider</c> call site — <c>dialect.TableReference(null, config.MetaTableDbName)</c>
/// currently hardcodes a null schema, so a meta table living outside the
/// default schema is looked up unqualified. That call site is owned by a
/// different agent; passing <c>config.TableSchema</c> there is a follow-up
/// one-line change, not made here.
/// </remarks>
public record EavConfig(
    string MetaTableDbName,      // e.g., "wp_postmeta"
    string ParentTableDbName,    // e.g., "wp_posts"
    string ForeignKeyColumn,     // e.g., "post_id" — column in meta table referencing parent
    string KeyColumn,            // e.g., "meta_key"
    string ValueColumn,          // e.g., "meta_value"
    string TableSchema = "dbo"); // the meta table's own schema, e.g. "app"
