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
        public List<TableSqlData> TableSqlData { get; }
        public List<TableSqlData> FragmentData { get; }
        public Stack<TableSqlData> CurrentTables { get; }
        public Stack<TableJoin> CurrentJoins { get; }
        public Cleanup AddSetter(Action<object?>? setter);
        public void Set(object? value);
        public void PopSetter();

        public Cleanup StartTableArgument(string argumentName);
        void ReduceFragments();

        public void PushField(string name, string alias);
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
        public List<TableSqlData> TableSqlData { get; init; } = new List<TableSqlData>();
        public List<TableSqlData> FragmentData { get; init; } = new List<TableSqlData>();
        public Stack<TableSqlData> CurrentTables { get; init; } = new Stack<TableSqlData>();
        public Stack<TableJoin> CurrentJoins { get; init; } = new Stack<TableJoin>();
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

        public Cleanup StartTableArgument(string argumentName)
        {
            var table = CurrentTables.FirstOrDefault();
            return AddSetter(table?.GetArgumentSetter(argumentName));
        }

        public void ReduceFragments()
        {
            foreach (var table in TableSqlData)
            {
                ReduceFragments(table);
            }
        }

        private void ReduceFragments(TableSqlData table)
        {
            foreach (var spread in table.FragmentSpreads)
            {
                var fragment = FragmentData.First(x => x.TableName == spread.FragmentName);
                spread.Table = fragment;
                table.ColumnNames.AddRange(fragment.ColumnNames);
                table.Joins.AddRange(fragment.Joins.Select(tj =>
                {
                    var result = new TableJoin
                    {
                        Name = tj.Name,
                        ParentTable = table,
                        Alias = tj.Alias,
                        ParentColumn = tj.ParentColumn,
                        ChildTable = new TableSqlData
                        {
                            TableName = tj.ChildTable.TableName,
                            ColumnNames = tj.ChildTable.ColumnNames,
                            FragmentSpreads = tj.ChildTable.FragmentSpreads,
                            IsFragment = false,
                            Filter = tj.ChildTable.Filter,
                            Limit = tj.ChildTable.Limit,
                            Offset = tj.ChildTable.Offset,
                            Sort = tj.ChildTable.Sort,
                            IncludeResult = false,
                        },
                        ChildColumn = tj.ChildColumn,
                        JoinType = tj.JoinType,
                    };
                    result.ChildTable.Joins = tj.ChildTable.Joins.Select(j => new TableJoin
                    {
                        Name = j.Name,
                        Alias = j.Alias,
                        ChildColumn = j.ChildColumn,
                        ChildTable = j.ChildTable,
                        ParentTable = result.ChildTable,
                        ParentColumn = j.ParentColumn,
                        JoinType = j.JoinType,
                    }).ToList();
                    ReduceFragments(result.ChildTable);
                    return result;
                }));
            }
        }

        public List<TableSqlData> GetFinalTables()
        {
            return Fields.Select(f => f.ToSqlData()).ToList();
        }

        public void PushField(string name, string alias)
        {
            var field = new Field { Name = name, Alias = alias };
            if (FieldsStack.Any() == false)
            {
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
        public string Alias { get; init; } = string.Empty;
        public string Name { get; init; } = null!;
        public object? Value { get; set; }
        public List<Field> Fields { get; init; } = new List<Field>();
        public List<Argument> Arguments { get; init; } = new List<Argument>();
        public List<string> Fragments { get; init; } = new List<string>();
        public override string ToString() => $"{Alias}:{Name}={Value}({Arguments.Count})/{Fields.Count}/{Fragments.Count}";

        public TableSqlData ToSqlData()
        {
            //TODO: Replace this with a global map between SQL names and graphql names
            var name = Name.Replace("_join_", "").Replace("__", " ");
            var rawSort = (List<object?>?) Arguments.FirstOrDefault(a => a.Name == "sort")?.Value;
            var sort = rawSort?.Cast<string>()?.ToList() ?? new List<string>();
            var result =  new TableSqlData
            {
                Alias = Alias,
                TableName = name,
                IsFragment = false,
                IncludeResult = true,
                ColumnNames = Fields.Where(f => f.Fields.Any() == false).Select(f => f.Name).ToList(),
                Sort = sort,
                Limit = (int?)Arguments.FirstOrDefault(a => a.Name == "limit")?.Value,
                Offset = (int?)Arguments.FirstOrDefault(a => a.Name == "offset")?.Value,
                Filter = TableFilter.FromObject(Arguments.FirstOrDefault(a => a.Name == "filter")?.Value),
                Links = Fields
                            .Where((f) => f.Fields.Any() && f.Name.StartsWith("_join_") == false)
                            .Select(f => f.ToSqlData())
                            .ToList(),
            };
            result.Joins.AddRange(
                Fields
                    .Where((f) => f.Fields.Any() && f.Name.StartsWith("_join_") == true)
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
                ParentTable = parent,
                ChildTable = ToSqlData(),
                ParentColumn = columns[0],
                ChildColumn= columns[1],
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
