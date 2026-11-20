namespace BifrostQL.Core.Resolvers
{
    /// <summary>The kind of database constraint a write broke, read from the driver's SQLSTATE.</summary>
    public enum DatabaseConstraintKind
    {
        /// <summary>A unique or primary-key constraint (SQLSTATE <c>23505</c>, or a driver's duplicate-key code).</summary>
        Unique,

        /// <summary>A foreign-key constraint (SQLSTATE <c>23503</c>).</summary>
        ForeignKey,

        /// <summary>A <c>CHECK</c> constraint (SQLSTATE <c>23514</c>).</summary>
        Check,

        /// <summary>An exclusion constraint (SQLSTATE <c>23P01</c>).</summary>
        Exclusion,
    }

    /// <summary>
    /// A constraint violation as an <see cref="IDatabaseErrorMapper"/> sees it: the kind, and the
    /// constraint's and table's names where the driver reports them (PostgreSQL does; SQL Server,
    /// MySQL and SQLite report only a unique violation, with no names). Never the statement or the
    /// refused values.
    /// </summary>
    public sealed record DatabaseConstraintViolation(
        DatabaseConstraintKind Kind,
        string? ConstraintName,
        string? TableSchema,
        string? TableName);

    /// <summary>
    /// A host's answer to a constraint violation, registered with
    /// <c>AddBifrostDatabaseErrorMapper&lt;T&gt;</c>. BifrostQL asks it when a GraphQL field (a
    /// generated write, a <c>_batch</c>, a tree write or a server operation) fails on a constraint,
    /// and raises the error it returns in place of its own answer (<c>CONFLICT</c>,
    /// <c>FOREIGN_KEY_VIOLATION</c>, <c>EXCLUSION_VIOLATION</c>, or <c>DATABASE_ERROR</c> for a
    /// check). Return null to keep BifrostQL's answer. Mappers are asked in registration order and
    /// the first non-null answer wins. The returned error's message and code reach the client
    /// verbatim, so they must carry no value the caller sent.
    /// </summary>
    public interface IDatabaseErrorMapper
    {
        BifrostExecutionError? Map(DatabaseConstraintViolation violation);
    }
}
