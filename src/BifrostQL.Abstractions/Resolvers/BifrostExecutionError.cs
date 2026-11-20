using System;
using System.Data.Common;

namespace BifrostQL.Core.Resolvers
{
    /// <summary>
    /// Bifrost-native execution error that replaces GraphQL.NET's ExecutionError.
    /// Represents an error encountered during field resolution or query execution.
    /// </summary>
    public class BifrostExecutionError : Exception
    {
        public BifrostExecutionError(string message)
            : base(message)
        {
        }

        public BifrostExecutionError(string message, Exception innerException)
            : base(message, innerException)
        {
        }

        /// <summary>
        /// The field path where the error occurred, for structured error reporting.
        /// </summary>
        public IReadOnlyList<object>? ErrorPath { get; init; }

        /// <summary>
        /// Additional metadata about the error (error codes, hints, etc.).
        /// </summary>
        public IDictionary<string, object?>? Extensions { get; set; }
        
        /// <summary>
        /// Error code for programmatic handling.
        /// </summary>
        public string? ErrorCode { get; init; }

        /// <summary>
        /// The name of the database constraint that refused the write, for a
        /// <see cref="ConflictCode"/>, <see cref="ExclusionViolationCode"/> or
        /// <see cref="ForeignKeyViolationCode"/> error whose driver reports it (PostgreSQL does).
        /// It reaches the client as <c>extensions.constraint</c>: the schema author chose the name,
        /// and it is the one stable handle a client has to say which rule a write broke. The
        /// statement and the refused values never travel with it.
        /// </summary>
        public string? ConstraintName { get; init; }

        /// <summary>
        /// The constraint violation this error reports, set by <see cref="FromDatabaseException"/>
        /// for a unique, foreign-key, check or exclusion violation. It is server-side only: a
        /// host's <see cref="IDatabaseErrorMapper"/> reads it to answer with its own code, and
        /// it never reaches the wire (only <see cref="ConstraintName"/> does, for the kinds that
        /// already carried it).
        /// </summary>
        public DatabaseConstraintViolation? ConstraintViolation { get; init; }

        /// <summary>
        /// <see cref="ErrorCode"/> for a unique or primary-key violation (PostgreSQL SQLSTATE
        /// <c>23505</c>), and for other conflicts with the stored rows.
        /// </summary>
        public const string ConflictCode = "CONFLICT";

        /// <summary>
        /// <see cref="ErrorCode"/> for an exclusion-constraint violation (PostgreSQL SQLSTATE
        /// <c>23P01</c>): the row conflicts with a stored one under the constraint's operators,
        /// such as two overlapping bookings of one room.
        /// </summary>
        public const string ExclusionViolationCode = "EXCLUSION_VIOLATION";

        /// <summary>
        /// <see cref="ErrorCode"/> for a foreign-key violation (SQLSTATE <c>23503</c>): the row
        /// refers to one that does not exist, or the write removes one still referenced.
        /// </summary>
        public const string ForeignKeyViolationCode = "FOREIGN_KEY_VIOLATION";

        /// <summary>
        /// <see cref="ErrorCode"/> marking a read rejected by an authorization
        /// transformer (tenant scoping, row/column policy). It distinguishes a
        /// fail-closed access denial — which callers such as the MCP search tool
        /// treat as "table not visible, skip" — from validation or database
        /// failures, which must propagate rather than be swallowed as "no match".
        /// </summary>
        public const string AccessDeniedCode = "ACCESS_DENIED";

        /// <summary>
        /// <see cref="ErrorCode"/> marking an address that names nothing the caller can see: an
        /// unknown table or key, a malformed address, or a row scoped away by the read pipeline.
        /// The file-object seam raises it so a wire mapper can answer "not found" by condition
        /// rather than by exception type, and tell it apart from a fault that must answer 500.
        /// </summary>
        public const string NotFoundCode = "NOT_FOUND";

        /// <summary>
        /// <see cref="ErrorCode"/> marking a value the database could not read as its column's
        /// type: a malformed uuid, a bad date, a number out of range. The caller can fix it, so
        /// it is not a <c>DATABASE_ERROR</c>. It is the code GraphQL.NET already answers with when
        /// a variable fails to coerce, so a client handles one word for both.
        /// </summary>
        public const string InvalidValueCode = "INVALID_VALUE";

        /// <summary>
        /// <see cref="ErrorCode"/> for a statement the database cancelled at its own time limit
        /// (<see cref="IsStatementTimeout"/>). The GraphQL HTTP middleware throws such a request to
        /// the host as <see cref="BifrostStatementTimeoutException"/>; every other front door
        /// answers with this code, which the caller may retry.
        /// </summary>
        public const string StatementTimeoutCode = "STATEMENT_TIMEOUT";

        /// <summary>Message for a <see cref="StatementTimeoutCode"/> error.</summary>
        public const string StatementTimeoutMessage = "The database cancelled the statement at its time limit.";
        
        /// <summary>
        /// Suggested actions the user can take to resolve this error.
        /// </summary>
        public IReadOnlyList<string>? SuggestedActions { get; init; }

        /// <summary>
        /// Creates a connection-related error with helpful suggestions.
        /// </summary>
        public static BifrostExecutionError ConnectionFailed(string details, Exception? inner = null)
        {
            var message = $"Database connection failed: {details}";
            var extensions = new Dictionary<string, object?>
            {
                ["errorCode"] = "CONNECTION_FAILED",
                ["suggestions"] = new[]
                {
                    "Verify your connection string is correct",
                    "Ensure the database server is running and accessible",
                    "Check firewall settings allow connections on the database port",
                    "Verify the database name exists"
                }
            };
            
            var ex = inner != null 
                ? new BifrostExecutionError(message, inner) { Extensions = extensions }
                : new BifrostExecutionError(message) { Extensions = extensions };
            
            return ex;
        }

        /// <summary>
        /// Creates a schema-related error.
        /// </summary>
        public static BifrostExecutionError SchemaError(string details, string? tableName = null)
        {
            return new BifrostExecutionError($"Schema error: {details}")
            {
                Extensions = new Dictionary<string, object?>
                {
                    ["errorCode"] = "SCHEMA_ERROR",
                    ["tableName"] = tableName
                }
            };
        }

        /// <summary>Environment variable that opts into surfacing raw database
        /// error text to clients (local debugging only).</summary>
        public const string ExposeDbErrorsEnvVar = "BIFROST_EXPOSE_DB_ERRORS";

        /// <summary>Safe, client-facing message for a unique/duplicate-key
        /// violation. Carries no schema detail yet still conveys the actionable
        /// cause; downstream classification keys off <see cref="ErrorCode"/>.</summary>
        public const string ConflictMessage = "A conflicting record already exists.";

        /// <summary>The safe message for <see cref="ExclusionViolationCode"/>.</summary>
        public const string ExclusionViolationMessage = "The change conflicts with an existing record.";

        /// <summary>The safe message for <see cref="ForeignKeyViolationCode"/>.</summary>
        public const string ForeignKeyViolationMessage =
            "The change refers to a record that does not exist, or removes one that is still referenced.";

        /// <summary>The safe message for <see cref="InvalidValueCode"/>. The driver's own text echoes
        /// the value and the column's type, so it is not forwarded.</summary>
        public const string InvalidValueMessage = "A value could not be read as its column's type.";

        /// <summary>
        /// True when the exception chain holds a driver error whose code says a unique or primary-key
        /// constraint refused the write. Read from the driver's code, never its text: drivers echo the
        /// caller's values (Postgres 22P02, MySQL 1366, SQL Server 2628 all quote the rejected value),
        /// so a phrase match calls any error that quotes "duplicate key" a conflict. The codes, as the
        /// pinned drivers report them:
        /// <list type="bullet">
        /// <item>PostgreSQL: SqlState <c>23505</c>.</item>
        /// <item>MySQL: SqlState <c>23000</c>, which foreign-key and not-null failures share, with
        /// server error 1062 in <c>Data["Server Error Code"]</c>.</item>
        /// <item>SQL Server: no SqlState; error 2627 (constraint) or 2601 (unique index) in
        /// <c>Data["HelpLink.EvtID"]</c>.</item>
        /// <item>SQLite: no SqlState and nothing in <c>Data</c>; extended code 2067 (unique) or 1555
        /// (primary key) on <c>SqliteException.SqliteExtendedErrorCode</c>, read by name because this
        /// assembly references no driver. A trigger's RAISE text has the same message wrapper as the
        /// engine's unique check, so the text cannot stand in for the code.</item>
        /// </list>
        /// Pinned against the real engines by <c>ConstraintViolationClassificationTests</c>.
        /// </summary>
        public static bool IsUniqueViolation(Exception? ex)
        {
            for (var current = ex; current != null; current = current.InnerException)
            {
                if (current is DbException db && IsUniqueViolationCode(db))
                    return true;
            }
            return false;
        }

        private static bool IsUniqueViolationCode(DbException db) => db.SqlState switch
        {
            UniqueViolationSqlState => true,
            MySqlIntegrityViolationSqlState => db.Data[MySqlServerErrorCodeKey] is MySqlDuplicateKeyErrorCode,
            null => db.Data[SqlServerErrorNumberKey] is SqlServerUniqueConstraintNumber or SqlServerUniqueIndexNumber
                || SqliteExtendedErrorCode(db) is SqliteUniqueExtendedCode or SqlitePrimaryKeyExtendedCode,
            _ => false,
        };

        /// <summary>The SQLite extended result code of a <c>SqliteException</c>, or null for any other error.</summary>
        private static int? SqliteExtendedErrorCode(DbException db)
        {
            var type = db.GetType();
            if (type.FullName != SqliteExceptionTypeName)
                return null;
            return type.GetProperty(SqliteExtendedErrorCodeProperty)?.GetValue(db) as int?;
        }

        /// <summary>MySQL SQLSTATE 23000, shared by duplicate-key, foreign-key and not-null failures.</summary>
        private const string MySqlIntegrityViolationSqlState = "23000";

        /// <summary>The <see cref="Exception.Data"/> key under which MySqlConnector carries the server error number.</summary>
        private const string MySqlServerErrorCodeKey = "Server Error Code";

        /// <summary>MySQL error 1062 <c>ER_DUP_ENTRY</c>.</summary>
        private const int MySqlDuplicateKeyErrorCode = 1062;

        /// <summary>The <see cref="Exception.Data"/> key under which Microsoft.Data.SqlClient carries the server error number.</summary>
        private const string SqlServerErrorNumberKey = "HelpLink.EvtID";

        /// <summary>SQL Server error 2627: violation of a PRIMARY KEY or UNIQUE constraint.</summary>
        private const string SqlServerUniqueConstraintNumber = "2627";

        /// <summary>SQL Server error 2601: duplicate key row in a unique index.</summary>
        private const string SqlServerUniqueIndexNumber = "2601";

        private const string SqliteExceptionTypeName = "Microsoft.Data.Sqlite.SqliteException";
        private const string SqliteExtendedErrorCodeProperty = "SqliteExtendedErrorCode";

        /// <summary>SQLite <c>SQLITE_CONSTRAINT_UNIQUE</c>.</summary>
        private const int SqliteUniqueExtendedCode = 2067;

        /// <summary>SQLite <c>SQLITE_CONSTRAINT_PRIMARYKEY</c>.</summary>
        private const int SqlitePrimaryKeyExtendedCode = 1555;

        private const string UniqueViolationSqlState = "23505";
        private const string ExclusionViolationSqlState = "23P01";
        private const string ForeignKeyViolationSqlState = "23503";
        private const string CheckViolationSqlState = "23514";

        /// <summary>
        /// The <see cref="Exception.Data"/> keys under which Npgsql's <c>PostgresException</c>
        /// carries the violated table's schema and name.
        /// </summary>
        private const string DriverSchemaNameKey = "SchemaName";
        private const string DriverTableNameKey = "TableName";

        /// <summary>
        /// The <see cref="Exception.Data"/> key under which Npgsql's <c>PostgresException</c>
        /// carries the constraint name. Read from <c>Data</c> because this assembly references
        /// no driver.
        /// </summary>
        private const string DriverConstraintNameKey = "ConstraintName";

        /// <summary>
        /// The first driver error in the chain whose SQLSTATE is a unique, exclusion or foreign-key
        /// violation. Read from the SQLSTATE rather than the text, which echoes the values. A driver
        /// that reports no such SQLSTATE (SQL Server, SQLite, and MySQL's shared <c>23000</c>) is
        /// left to <see cref="IsUniqueViolation"/>.
        /// </summary>
        private static DbException? FindIntegrityViolation(Exception ex)
        {
            for (var current = ex; current != null; current = current.InnerException)
            {
                if (current is DbException { SqlState: UniqueViolationSqlState or ExclusionViolationSqlState or ForeignKeyViolationSqlState } db)
                    return db;
            }
            return null;
        }

        /// <summary>
        /// True when the exception chain holds a driver error whose SQLSTATE says a value could not
        /// be read as its column's type (class 22, the data exceptions a bad input raises). Read from
        /// the SQLSTATE rather than the text, which echoes the value. SQL Server reports no SQLSTATE,
        /// so its conversion errors stay <c>DATABASE_ERROR</c>.
        /// </summary>
        public static bool IsInvalidValue(Exception? ex)
        {
            for (var current = ex; current != null; current = current.InnerException)
            {
                if (current is DbException { SqlState: "22P02" or "22007" or "22008" or "22003" })
                    return true;
            }
            return false;
        }

        /// <summary>
        /// PostgreSQL's <c>query_canceled</c>: raised by <c>statement_timeout</c>, and also by a
        /// cancel the client sent, which is why <see cref="IsStatementTimeout"/> is asked only
        /// while the request's own token is live.
        /// </summary>
        private const string QueryCanceledSqlState = "57014";

        /// <summary>
        /// The Win32 error a SQL Server client command timeout carries as its inner exception
        /// (<c>WAIT_TIMEOUT</c>), the documented way to tell it from other <c>SqlException</c>s
        /// without referencing the driver.
        /// </summary>
        private const int WaitTimeoutNativeError = 258;

        /// <summary>
        /// True when the exception chain says the database, or the driver's command timeout, ended
        /// a statement at its time limit: SQLSTATE <c>57014</c> (PostgreSQL <c>statement_timeout</c>),
        /// a <see cref="TimeoutException"/> (Npgsql's and MySqlConnector's command timeouts) or a
        /// Win32 <c>WAIT_TIMEOUT</c> (SQL Server's). A query cancel also raises <c>57014</c>, so a
        /// caller asks this only when the request's own token is not cancelled. MySQL's server-side
        /// <c>max_execution_time</c> (error 3024, SQLSTATE <c>HY000</c>) is not recognised: its
        /// SQLSTATE is shared with unrelated errors.
        /// </summary>
        public static bool IsStatementTimeout(Exception? ex)
        {
            for (var current = ex; current != null; current = current.InnerException)
            {
                switch (current)
                {
                    case DbException { SqlState: QueryCanceledSqlState }:
                    case TimeoutException:
                    case System.ComponentModel.Win32Exception { NativeErrorCode: WaitTimeoutNativeError }:
                        return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Wraps an exception raised while executing SQL for client return.
        /// Intentional <see cref="BifrostExecutionError"/>s (validation, tenant,
        /// policy) carry safe, authored messages and pass through unchanged.
        /// Raw driver/database exceptions can expose schema names, identifiers,
        /// and infrastructure detail, so their message is replaced with a generic
        /// one; the original exception is retained as <see cref="Exception.InnerException"/>
        /// for server-side logging. Set <see cref="ExposeDbErrorsEnvVar"/> to
        /// <c>1</c>/<c>true</c> to surface the raw text for local debugging.
        /// </summary>
        public static BifrostExecutionError FromDatabaseException(Exception ex)
        {
            if (ex is BifrostExecutionError bifrost)
                return bifrost;

            // A constraint violation is client-actionable, not an infrastructure leak —
            // surface a safe, stable message, a code per kind of constraint and the
            // constraint's name, so callers (e.g. workflow steps mapping to HTTP 409) can
            // classify it without string-matching raw driver text.
            if (FindIntegrityViolation(ex) is { } violation)
            {
                var (code, safeMessage) = violation.SqlState switch
                {
                    UniqueViolationSqlState => (ConflictCode, ConflictMessage),
                    ExclusionViolationSqlState => (ExclusionViolationCode, ExclusionViolationMessage),
                    _ => (ForeignKeyViolationCode, ForeignKeyViolationMessage),
                };
                var kind = violation.SqlState switch
                {
                    UniqueViolationSqlState => DatabaseConstraintKind.Unique,
                    ExclusionViolationSqlState => DatabaseConstraintKind.Exclusion,
                    _ => DatabaseConstraintKind.ForeignKey,
                };
                return new BifrostExecutionError(safeMessage, ex)
                {
                    ErrorCode = code,
                    ConstraintName = violation.Data[DriverConstraintNameKey] as string,
                    ConstraintViolation = ViolationOf(kind, violation),
                };
            }

            if (IsUniqueViolation(ex))
                return new BifrostExecutionError(ConflictMessage, ex)
                {
                    ErrorCode = ConflictCode,
                    // SQL Server, MySQL and SQLite name neither the constraint nor the table.
                    ConstraintViolation = new DatabaseConstraintViolation(DatabaseConstraintKind.Unique, null, null, null),
                };

            if (IsInvalidValue(ex))
                return new BifrostExecutionError(InvalidValueMessage, ex) { ErrorCode = InvalidValueCode };

            if (IsStatementTimeout(ex))
                return new BifrostExecutionError(StatementTimeoutMessage, ex) { ErrorCode = StatementTimeoutCode };

            var expose = Environment.GetEnvironmentVariable(ExposeDbErrorsEnvVar);
            var reveal = string.Equals(expose, "1", StringComparison.Ordinal)
                || string.Equals(expose, "true", StringComparison.OrdinalIgnoreCase);

            var message = reveal
                ? $"Database error: {ex.Message}"
                : "A database error occurred while processing the request.";

            return new BifrostExecutionError(message, ex)
            {
                ErrorCode = "DATABASE_ERROR",
                // A check violation keeps the generic answer unless a host maps it.
                ConstraintViolation = FindSqlState(ex, CheckViolationSqlState) is { } check
                    ? ViolationOf(DatabaseConstraintKind.Check, check)
                    : null,
            };
        }

        private static DatabaseConstraintViolation ViolationOf(DatabaseConstraintKind kind, DbException driver)
            => new(kind,
                driver.Data[DriverConstraintNameKey] as string,
                driver.Data[DriverSchemaNameKey] as string,
                driver.Data[DriverTableNameKey] as string);

        private static DbException? FindSqlState(Exception ex, string sqlState)
        {
            for (var current = ex; current != null; current = current.InnerException)
            {
                if (current is DbException db && db.SqlState == sqlState)
                    return db;
            }
            return null;
        }

        /// <summary>
        /// Creates a query validation error.
        /// </summary>
        public static BifrostExecutionError QueryError(string details, string? queryPath = null)
        {
            return new BifrostExecutionError($"Query error: {details}")
            {
                ErrorPath = queryPath != null ? new[] { queryPath } : null,
                Extensions = new Dictionary<string, object?>
                {
                    ["errorCode"] = "QUERY_ERROR"
                }
            };
        }
    }
}
