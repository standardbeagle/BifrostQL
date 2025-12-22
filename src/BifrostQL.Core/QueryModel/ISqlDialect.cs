namespace BifrostQL.Core.QueryModel;

public enum LikePatternType { Contains, StartsWith, EndsWith }

public interface ISqlDialect
{
    string EscapeIdentifier(string identifier);
    string TableReference(string? schema, string tableName);
    string Pagination(IEnumerable<string>? sortColumns, int? offset, int? limit);
    string ParameterPrefix { get; }
    string LastInsertedIdentity { get; }
    string LikePattern(string paramName, LikePatternType patternType);
    string GetOperator(string op);
}
