using GraphQL.Types;
using GraphQL.Validation;
using GraphQLParser.AST;
using GraphQLParser.Visitors;
using System.Runtime.CompilerServices;
using System.Text.Json;
using static GraphQLProxy.DbTableResolver;
using static GraphQLProxy.SqlContext;

namespace GraphQLProxy
{

    public interface ISqlContext : IASTVisitorContext
    {
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

        private void MatchFragments()
        {
            foreach(var table in TableSqlData)
            {
                foreach(var spread in table.FragmentSpreads)
                {
                    var fragment = FragmentData.First(x => x.TableName == spread.FragmentName);
                    spread.Table = fragment;
                    table.ColumnNames.AddRange(fragment.ColumnNames);
                    table.Joins.AddRange(fragment.Joins.Select(tj => new TableJoin
                    {
                        Name = tj.Name,
                        ParentTable = table,
                        Alias = tj.Alias,
                        ParentColumn = tj.ParentColumn,
                        ChildTable = tj.ChildTable,
                        ChildColumn = tj.ChildColumn,
                        JoinType = tj.JoinType,
                    }));
                }
            }

        }

        public List<TableSqlData> GetFinalTables()
        {
            MatchFragments();
            return TableSqlData;
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

    public class SqlVisitor : ASTVisitor<ISqlContext>
    {
        protected async override ValueTask VisitFieldAsync(GraphQLField field, ISqlContext context)
        {
            if (field == null)
                return;
            TableSqlData? table = null;
            TableJoin? join = null;
            TableSqlData? childTable = null;
            if (!context.CurrentTables.Any())
            {
                table = new TableSqlData()
                {
                    TableName = field.Name.StringValue,
                    IsFragment = false
                };
                
                context.TableSqlData.Add(table);
                context.CurrentTables.Push(table);
            } else 
            {
                var parent = context.CurrentTables.First();
                if (field.Name.StringValue.StartsWith("_join"))
                {
                    childTable = new TableSqlData
                    {
                        TableName = field.Name.StringValue.Replace("_join_", ""),
                        IsFragment = false,
                    };
                    join = new TableJoin
                    {
                        Name = field.Name.StringValue,
                        ParentTable = parent,
                        Alias = field.Alias?.Name?.StringValue,
                        ChildTable = childTable,
                    };
                    childTable.ParentJoin = join;
                    parent.Joins.Add(join);
                    context.CurrentTables.Push(childTable);
                    context.CurrentJoins.Push(join);
                } else
                {
                    parent.ColumnNames.Add(field.Name.StringValue);
                }
            }

            await base.VisitFieldAsync(field, context);
            if (table != null)
                context.CurrentTables.Pop();
            if (join != null)
                context.CurrentJoins.Pop();
            if (childTable != null)
                context.CurrentTables.Pop();
        }
        protected async override ValueTask VisitArgumentAsync(GraphQLArgument argument, ISqlContext context)
        {
            using var cleanup = context.StartTableArgument(argument.Name.StringValue);
            await base.VisitArgumentAsync(argument, context);
        }

        protected override async ValueTask VisitObjectValueAsync(GraphQLObjectValue objectValue, ISqlContext context)
        {
            var result = new Dictionary<string, object?>();
            context.FieldSetters.Push((key, value) => result[key] = value);
            await base.VisitObjectValueAsync(objectValue, context);
            context.FieldSetters.Pop();
            context.Setters.FirstOrDefault()?.Invoke(result);
        }

        protected override async ValueTask VisitObjectFieldAsync(GraphQLObjectField objectField, ISqlContext context)
        {
            context.Setters.Push(value => context.FieldSetters.FirstOrDefault()?.Invoke(objectField.Name.StringValue, value));
            await base.VisitObjectFieldAsync(objectField, context);
            context.Setters.Pop();
        }

        protected async override ValueTask VisitVariableAsync(GraphQLVariable variable, ISqlContext context)
        {
            var foundVariable = context.Variables.FirstOrDefault(v => v.Name == variable.Name.StringValue);
            if (foundVariable == null)
                throw new ArgumentException($"no data provided for variable: {variable.Name.StringValue}");
            context.Setters.FirstOrDefault()?.Invoke(foundVariable?.Value);
            await base.VisitVariableAsync(variable, context);
        }

        protected async override ValueTask VisitFragmentSpreadAsync(GraphQLFragmentSpread spread, ISqlContext context)
        {
            var table = context.CurrentTables.FirstOrDefault();
            if (table != null)
                table.FragmentSpreads.Add(new FragmentSpread { FragmentName = spread.FragmentName.Name.StringValue });
            await base.VisitFragmentSpreadAsync(spread, context);
        }

        protected async override ValueTask VisitFragmentDefinitionAsync(GraphQLFragmentDefinition fragmentDefinition, ISqlContext context)
        {
            var table = new TableSqlData()
            {
                TableName = fragmentDefinition.FragmentName.Name.StringValue,
                IsFragment = true,
            };

            context.FragmentData.Add(table);
            context.CurrentTables.Push(table);
            await base.VisitFragmentDefinitionAsync(fragmentDefinition, context);
            context.CurrentTables.Pop();
        }

        protected override ValueTask VisitIntValueAsync(GraphQLIntValue value, ISqlContext context)
        {
            context.Set(Convert.ToInt32(value.Value.ToString()));
            return base.VisitIntValueAsync(value, context);
        }
        protected override ValueTask VisitFloatValueAsync(GraphQLFloatValue value, ISqlContext context)
        {
            context.Set(Convert.ToDouble(value.Value.ToString()));
            return base.VisitFloatValueAsync(value, context);
        }
        protected override async ValueTask VisitStringValueAsync(GraphQLStringValue stringValue, ISqlContext context)
        {
            context.Set(stringValue.Value.Span.ToString());
            await base.VisitStringValueAsync(stringValue, context);
        }

        protected override ValueTask VisitBooleanValueAsync(GraphQLBooleanValue value, ISqlContext context)
        {
            context.Set(value.BoolValue);
            return base.VisitBooleanValueAsync(value, context);
        }
        protected override ValueTask VisitNullValueAsync(GraphQLNullValue value, ISqlContext context)
        {
            context.Set(null);
            return base.VisitNullValueAsync(value, context);
        }
        protected override ValueTask VisitEnumValueAsync(GraphQLEnumValue value, ISqlContext context)
        {
            context.Set(value.Name.StringValue);
            return base.VisitEnumValueAsync(value, context);
        }
        protected async override ValueTask VisitListValueAsync(GraphQLListValue listType, ISqlContext context)
        {
            var result = new List<object?>();
            context.Setters.Push(value => result.Add(value));
            await base.VisitListValueAsync(listType, context);
            context.Setters.Pop();
            context.Setters.FirstOrDefault()?.Invoke(result);
        }

    }
}
