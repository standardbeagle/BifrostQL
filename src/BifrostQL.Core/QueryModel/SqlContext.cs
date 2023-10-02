using BifrostQL.Core.Model;
using GraphQL;
using GraphQL.Validation;
using GraphQLParser.Visitors;

namespace BifrostQL.Core.QueryModel
{

    public interface ISqlContext : IASTVisitorContext
    {
        public List<IField> Fields { get; }
        public Variables Variables { get; }
        public Stack<Action<string, object?>> FieldSetters { get; }
        public Stack<Action<object?>> Setters { get; }
        public void Set(object? value);
        public void PushField(string name, string? alias);
        public void PopField();
        public void AddValue(object? value);
        public void AddFragmentSpread(string name);
        public void PushFragment(string name, string alias);
        public void PopFragment();
        public void PushArgument(string name);
        public void PopArgument();
        public void SyncFragments();
    }

    public class SqlContext : ISqlContext
    {
        public Variables Variables { get; init; } = null!;
        public CancellationToken CancellationToken { get; init; }
        public Stack<Action<string, object?>> FieldSetters { get; init; } = new();
        public Stack<Action<object?>> Setters { get; init; } = new();
        public List<IField> Fields { get; init; } = new();
        public List<IField> Fragments { get; init; } = new();
        public Stack<IField> FieldsStack { get; init; } = new();
        public Argument? CurrentArgument { get; set; }
        public void Set(object? value)
        {
            Setters.FirstOrDefault()?.Invoke(value);
        }
        public List<GqlObjectQuery> GetFinalTables(IDbModel model)
        {
            return Fields.Select(f => f.ToSqlData(model)).ToList();
        }
        public void PushField(string name, string? alias)
        {
            var field = new Field { Name = name, Alias = alias };
            if (FieldsStack.Any() == false)
            {
                field.IncludeResult = true;
                Fields.Add(field);
            }
            else
            {
                FieldsStack.First().Fields.Add(field);
            }
            FieldsStack.Push(field);
        }
        public void PopField() { FieldsStack.Pop(); }
        public void AddValue(object? value)
        {
            if (CurrentArgument != null)
            {
                CurrentArgument.Value = value;
                return;
            }
            if (FieldsStack.Any() == false)
                return;
            FieldsStack.First().Value = value;
        }
        public void AddFragmentSpread(string name) { FieldsStack.First().Fragments.Add(name); }
        public void PushFragment(string name, string alias)
        {
            var field = new Field { Name = name, Alias = alias };
            if (FieldsStack.Any() == false)
            {
                Fragments.Add(field);
            }
            else
            {
                FieldsStack.First().Fields.Add(field);
            }
            FieldsStack.Push(field);
        }
        public void PopFragment() { FieldsStack.Pop(); }
        public void PushArgument(string name)
        {
            var arg = new Argument { Name = name };
            FieldsStack.First().Arguments.Add(arg);
            CurrentArgument = arg;
        }
        public void PopArgument() { CurrentArgument = null; }

        public void SyncFragments()
        {
            var fragmentLookup = Fragments.ToDictionary(f => f.Name);
            foreach (var field in Fields)
            {
                SyncFieldFragments(field, fragmentLookup);
            }
        }

        public void SyncFieldFragments(IField field, IDictionary<string, IField> fragmentList)
        {
            foreach (var fragmentField in field.Fragments.Select(f => fragmentList[f]).SelectMany(f => f.Fields))
            {
                field.Fields.Add(CopyField(fragmentField));
            }
            foreach (var subField in field.Fields)
            {
                SyncFieldFragments(subField, fragmentList);
            }
        }

        public IField CopyField(IField field)
        {
            return new Field()
            {
                Name = field.Name,
                Alias = field.Alias,
                Value = field.Value,
                Arguments = field.Arguments,
                Fields = field.Fields.Select(CopyField).ToList(),
                Fragments = field.Fragments,
            };
        }
    }
    public class Cleanup : IDisposable
    {
        public static readonly Cleanup Skip = new();
        public Action? Action { get; init; }
        public void Dispose()
        {
            GC.SuppressFinalize(this);
            Action?.Invoke();
        }
    }

    public interface IField
    {
        string? Alias { get; init; }
        string Name { get; init; }
        object? Value { get; set; }
        bool IncludeResult { get; set; }
        List<IField> Fields { get; init; }
        List<Argument> Arguments { get; init; }
        List<string> Fragments { get; init; }
        string ToString();
        GqlObjectQuery ToSqlData(IDbModel model, IField? parent = null, string basePath = "");
        TableJoin ToJoin(IDbModel model, GqlObjectQuery parent);
    }

    public sealed class Field : IField
    {
        public string? Alias { get; init; }
        public string Name { get; init; } = null!;
        public object? Value { get; set; }
        public bool IncludeResult { get; set; }
        public List<IField> Fields { get; init; } = new();
        public List<Argument> Arguments { get; init; } = new();
        public List<string> Fragments { get; init; } = new();
        public override string ToString() => $"{Alias}:{Name}={Value}({Arguments.Count})/{Fields.Count}/{Fragments.Count}";

        private static bool IsSpecialColumn(string name)
        {
            return name.StartsWith("_join_") || name.StartsWith("_single_");
        }

        public GqlObjectQuery ToSqlData(IDbModel model, IField? parent = null, string basePath = "")
        {
            var path = basePath + "/" + Alias;
            var tableName = Name.Replace("_join_", "").Replace("_single_", "");
            var dbTable = model.GetTableByFullGraphQlName(tableName);
            var rawSort = (IEnumerable<object?>?) Arguments.FirstOrDefault(a => a.Name == "sort")?.Value;
            var sort = rawSort?.Cast<string>()?.ToList() ?? new List<string>();
            var dataFields = Fields.FirstOrDefault(f => f.Name == "data")?.Fields ?? new List<IField>();
            var fields = (IncludeResult ? dataFields : Fields).Where(f => !f.Name.StartsWith("__")).ToList();
            var result =  new GqlObjectQuery
            {
                Alias = Alias,
                TableName = dbTable.DbName,
                SchemaName = dbTable.TableSchema,
                GraphQlName = tableName,
                Path = path,
                IsFragment = false,
                IncludeResult = IncludeResult,
                Columns = fields.Where(f => f.Fields.Any() == false).Select(f => (f.Name, dbTable.GraphQlLookup[f.Name].DbName)).ToList(),
                Sort = sort,
                Limit = (int?)Arguments.FirstOrDefault(a => a.Name == "limit")?.Value,
                Offset = (int?)Arguments.FirstOrDefault(a => a.Name == "offset")?.Value,
                Filter = Arguments.Where(a => a is { Name: "filter", Value: not null }).Select(arg => TableFilter.FromObject(arg.Value, dbTable.DbName)).FirstOrDefault(),
                Links = fields
                            .Where((f) => f.Fields.Any() && IsSpecialColumn(f.Name) == false)
                            .Select(f => f.ToSqlData(model, this, path))
                            .ToList(),
            };
            result.Joins.AddRange(
                fields
                    .Where((f) => f.Fields.Any() && IsSpecialColumn(f.Name) == true)
                    .Select(f => f.ToJoin(model, result))
                );
            return result;
        }

        public TableJoin ToJoin(IDbModel model, GqlObjectQuery parent)
        {
            var onArg = Arguments.FirstOrDefault(a => a.Name == "on");

            if (onArg == null)
                throw new ExecutionError($"join on table {parent.GraphQlName} missing on argument.");

            var columns = (onArg.Value as IDictionary<string, object?>) ?? throw new ExecutionError($"While joining table {parent.GraphQlName}, unable to convert on value to object");
            if (columns.Keys.Count != 1)
                throw new ArgumentException("on joins only support two columns");
            var relation = columns.Values.First() as IDictionary<string, object?> ?? throw new ExecutionError($"While joining table {parent.GraphQlName}, unable to convert on value to a string");
            return new TableJoin
            {
                Name = Name,
                Alias = Alias,
                FromTable = parent,
                ConnectedTable = ToSqlData(model),
                FromColumn = columns.Keys.First(),
                ConnectedColumn = relation.Values?.First()?.ToString() ?? throw new ExecutionError($"While joining table {parent.GraphQlName}, unable to resolve join column {relation?.Keys?.FirstOrDefault()}"),
                Operator = relation.Keys.First(),
                JoinType = Name.StartsWith("_join_") ? JoinType.Join: JoinType.Single,
            };
        }
    }

    public sealed class Argument
    {
        public string Name { get; init; } = null!;
        public object? Value { get; set; }
        public override string ToString() => $"{Name}={Value}";
    }
}
