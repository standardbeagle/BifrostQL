namespace BifrostQL.Core.QueryModel;

public sealed class ParameterizedSql
{
    public static readonly ParameterizedSql Empty = new("", Array.Empty<SqlParameterInfo>());

    public string Sql { get; }
    public IReadOnlyList<SqlParameterInfo> Parameters { get; }

    public ParameterizedSql(string sql, IReadOnlyList<SqlParameterInfo> parameters)
    {
        Sql = sql ?? throw new ArgumentNullException(nameof(sql));
        Parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
    }

    public ParameterizedSql Append(ParameterizedSql other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new(Sql + other.Sql, Parameters.Concat(other.Parameters).ToList());
    }

    public ParameterizedSql Append(string sql) => new(Sql + sql, Parameters);
    public ParameterizedSql Prepend(string prefix) => new(prefix + Sql, Parameters);
}
