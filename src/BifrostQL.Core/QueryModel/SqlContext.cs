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
        /// <summary>
        /// Fragment spreads written at the operation root (<c>query { ...F }</c>), each with
        /// the index in <see cref="Fields"/> it was written at. A root spread has no enclosing
        /// field to record it on; <see cref="SyncFragments"/> splices its fields in as root fields.
        /// </summary>
        private readonly List<(int Position, string Name)> _rootFragmentSpreads = new();
        public QueryArgument? CurrentArgument { get; set; }
        public void Set(object? value)
        {
            Setters.FirstOrDefault()?.Invoke(value);
        }
        /// <summary>
        /// Builds one <see cref="GqlObjectQuery"/> per root field that names a table row
        /// query. Other root fields — introspection, the aggregate/pivot/history surfaces,
        /// `_rawQuery`, `_dbSchema` — are owned by their own resolvers and carry no table
        /// to resolve, so they are skipped here. Building them threw, and because every
        /// sibling root resolver awaits this one shared parse, that failed the whole
        /// document: a table root field alongside any of them came back null.
        /// </summary>
        public List<GqlObjectQuery> GetFinalQueries(IDbModel model)
        {
            return Fields
                .Where(f => QueryField.IsTableRootField(model, f))
                .Select(f => f.ToSqlData(model))
                .ToList();
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
        public void AddFragmentSpread(string name)
        {
            if (FieldsStack.Count == 0)
            {
                _rootFragmentSpreads.Add((Fields.Count, name));
                return;
            }
            FieldsStack.First().Fragments.Add(name);
        }
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
            // A root spread's fields become root fields, flagged as PushField flags a root
            // field written inline. Spliced from the last spread back, so each recorded
            // position still points where its spread was written.
            for (var i = _rootFragmentSpreads.Count - 1; i >= 0; i--)
            {
                var (position, name) = _rootFragmentSpreads[i];
                Fields.InsertRange(position, QueryField.FragmentFields(name, fragmentLookup)
                    .Select(f => (IQueryField)QueryField.CopyField(f, includeResult: true)));
            }
            _rootFragmentSpreads.Clear();
            foreach (var field in Fields)
            {
                QueryField.SyncFieldFragments(field, fragmentLookup);
            }
            var merged = QueryField.MergeSameResponseKey(Fields);
            Fields.Clear();
            Fields.AddRange(merged);
        }
    }
}
