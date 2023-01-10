using GraphQL.Validation;
using GraphQLParser.Visitors;
using GraphQLProxy.QueryModel;

namespace GraphQLProxy.QueryModel
{

    public interface ISqlContext : IASTVisitorContext
    {
        public List<Field> Fields { get; }
        public Variables Variables { get; }
        public Stack<Action<string, object?>> FieldSetters { get; }
        public Stack<Action<object?>> Setters { get; }
        public Cleanup AddSetter(Action<object?>? setter);
        public void Set(object? value);
        public void PopSetter();
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
        public Stack<Action<string, object?>> FieldSetters { get; init; } = new Stack<Action<string, object?>>();
        public Stack<Action<object?>> Setters { get; init; } = new Stack<Action<object?>>();
        public List<Field> Fields { get; init; } = new List<Field>();
        public List<Field> Fragments { get; init; } = new List<Field>();
        public Stack<Field> FieldsStack { get; init; } = new Stack<Field>();
        public Argument? CurrentArgument { get; set; }

        public Cleanup AddSetter(Action<object?>? setter)
        {
            if (setter == null) return Cleanup.Skip;
            Setters.Push(setter);
            return new Cleanup() { Action = PopSetter };
        }
        public void Set(object? value)
        {
            Setters.FirstOrDefault()?.Invoke(value);
        }
        public void PopSetter()
        {
            Setters.Pop();
        }

        public List<TableSqlData> GetFinalTables()
        {
            return Fields.Select(f => f.ToSqlData()).ToList();
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
            Argument arg = new Argument { Name = name };
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

        public void SyncFieldFragments(Field field, IDictionary<string, Field> fragmentList)
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

        public Field CopyField(Field field)
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
        public static Cleanup Skip = new Cleanup();
        public Action? Action { get; init; }
        public void Dispose()
        {
            Action?.Invoke();
        }
    }

    public sealed class Field
    {
        public string? Alias { get; init; }
        public string Name { get; init; } = null!;
        public object? Value { get; set; }
        public bool IncludeResult { get; set; }
        public List<Field> Fields { get; init; } = new List<Field>();
        public List<Argument> Arguments { get; init; } = new List<Argument>();
        public List<string> Fragments { get; init; } = new List<string>();
        public override string ToString() => $"{Alias}:{Name}={Value}({Arguments.Count})/{Fields.Count}/{Fragments.Count}";

        public static bool IsSpecialColumn(string name)
        {
            if (name.StartsWith("_join_")) return true;
            if (name.StartsWith("_single_")) return true; 
            return false;
        }
        public TableSqlData ToSqlData(Field? parent = null, string basePath = "")
        {
            var path = basePath + "/" + Alias ?? Name;
            //TODO: Replace this with a global map between SQL names and graphql names
            var name = Name.Replace("_join_", "").Replace("_single_", "").Replace("__", " ");
            var rawSort = (List<object?>?) Arguments.FirstOrDefault(a => a.Name == "sort")?.Value;
            var sort = rawSort?.Cast<string>()?.ToList() ?? new List<string>();
            var dataFields = Fields.FirstOrDefault(f => f.Name == "data")?.Fields ?? new List<Field>();
            var fields = IncludeResult ? dataFields : Fields;
            var result =  new TableSqlData
            {
                Alias = Alias,
                TableName = name,
                GraphQlName = Name,
                Path = path,
                IsFragment = false,
                IncludeResult = IncludeResult,
                ColumnNames = fields.Where(f => f.Fields.Any() == false).Select(f => f.Name).ToList(),
                Sort = sort,
                Limit = (int?)Arguments.FirstOrDefault(a => a.Name == "limit")?.Value,
                Offset = (int?)Arguments.FirstOrDefault(a => a.Name == "offset")?.Value,
                Filter = TableFilter.FromObject(Arguments.FirstOrDefault(a => a.Name == "filter")?.Value),
                Links = fields
                            .Where((f) => f.Fields.Any() && IsSpecialColumn(f.Name) == false)
                            .Select(f => f.ToSqlData(this, path))
                            .ToList(),
            };
            result.Joins.AddRange(
                fields
                    .Where((f) => f.Fields.Any() && IsSpecialColumn(f.Name) == true)
                    .Select(f => f.ToJoin(result))
                );
            return result;
        }

        public TableJoin ToJoin(TableSqlData parent)
        {
            var onArg = Arguments.FirstOrDefault(a => a.Name == "on");

            if (onArg == null)
                throw new ArgumentException("joins require columns specified with the on argument");

            var columns = (onArg.Value as IEnumerable<object?>)?.Cast<string>()?.ToArray() ?? throw new ArgumentException("on", "Unable to convert list");
            if (columns.Length != 2)
                throw new ArgumentException("on joins only support two columns");
            return new TableJoin
            {
                Name = Name,
                Alias = Alias,
                FromTable = parent,
                ConnectedTable = ToSqlData(),
                FromColumn = columns[0],
                ConnectedColumn= columns[1],
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
