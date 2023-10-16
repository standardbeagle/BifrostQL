using BifrostQL.Core.Model;

namespace BifrostQL.Core.QueryModel;

public interface ISqlGenerator
{
    void AddSql(IDbModel dbModel, IDictionary<string, string> sqls, TableJoin? parentJoin = null, QueryLink? parentQuery = null);
}

public interface ISqlQueryGenerator : ISqlGenerator
{
    string ToConnectedSql(IDbModel dbModel, string main, TableJoin tableJoin);
    IEnumerable<ISqlJoinGenerator> RecurseJoins { get; }
    void ConnectLinks(IDbModel dbModel, string basePath = "");
}

public interface ISqlJoin
{
    string? Alias { get; }
    string Name { get; }
    string JoinName { get; }
    public string FromColumn { get; init; }
    public QueryType QueryType { get; init; }
}

public interface ISqlJoinGenerator : ISqlGenerator, ISqlJoin
{
    ISqlQueryGenerator ConnectedTable { get; }
}

