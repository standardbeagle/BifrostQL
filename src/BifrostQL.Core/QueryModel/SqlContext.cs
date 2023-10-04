using BifrostQL.Core.Model;
using GraphQL;
using GraphQL.Validation;
using GraphQLParser.Visitors;

namespace BifrostQL.Core.QueryModel
{

    public interface ISqlContext : IASTVisitorContext
    {
        public List<IQueryField> Fields { get; }
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
        public List<IQueryField> Fields { get; init; } = new();
        public List<IQueryField> Fragments { get; init; } = new();
        public Stack<IQueryField> FieldsStack { get; init; } = new();
        public QueryArgument? CurrentArgument { get; set; }
        public void Set(object? value)
        {
            Setters.FirstOrDefault()?.Invoke(value);
        }
        public List<GqlObjectQuery> GetFinalQueries(IDbModel model)
        {
            return Fields.Select(f => f.ToSqlData(model)).ToList();
        }
        public void PushField(string name, string? alias)
        {
            var field = new QueryField { Name = name, Alias = alias };
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
            var field = new QueryField { Name = name, Alias = alias };
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
            var arg = new QueryArgument { Name = name };
            FieldsStack.First().Arguments.Add(arg);
            CurrentArgument = arg;
        }
        public void PopArgument() { CurrentArgument = null; }

        public void SyncFragments()
        {
            var fragmentLookup = Fragments.ToDictionary(f => f.Name);
            foreach (var field in Fields)
            {
                QueryField.SyncFieldFragments(field, fragmentLookup);
            }
        }
    }
}
