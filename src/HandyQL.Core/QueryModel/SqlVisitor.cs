using GraphQLParser.AST;
using GraphQLParser.Visitors;
using GraphQLProxy.QueryModel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GraphQLProxy.Core.QueryModel
{
    public class SqlVisitor : ASTVisitor<ISqlContext>
    {
        protected async override ValueTask VisitFieldAsync(GraphQLField field, ISqlContext context)
        {
            if (field == null)
                return;
            TableSqlData? table = null;
            TableJoin? join = null;
            TableSqlData? childTable = null;
            bool processingResultData = false;
            context.PushField(field.Name.StringValue, field.Alias?.Name?.StringValue ?? field.Name.StringValue);

            if (!context.CurrentTables.Any())
            {
                table = new TableSqlData()
                {
                    TableName = field.Name.StringValue,
                    Alias = field.Alias?.Name.StringValue ?? "",
                    IsFragment = false,
                    IncludeResult = true,
                };

                context.TableSqlData.Add(table);
                context.CurrentTables.Push(table);
            }
            else
            {
                var parent = context.CurrentTables.First();
                if (Field.IsSpecialColumn(field.Name.StringValue))
                {
                    childTable = new TableSqlData
                    {
                        TableName = field.Name.StringValue.Replace("_join_", ""),
                        IsFragment = false,
                        IncludeResult = false
                    };
                    join = new TableJoin
                    {
                        Name = field.Name.StringValue,
                        FromTable = parent,
                        Alias = field.Alias?.Name?.StringValue,
                        ConnectedTable = childTable,
                    };
                    childTable.JoinFrom = join;
                    parent.Joins.Add(join);
                    context.CurrentTables.Push(childTable);
                    context.CurrentJoins.Push(join);
                }
                else
                {
                    if (parent.IncludeResult && !parent.ProcessingResultData)
                    {
                        if (field.Name.StringValue == "data")
                        {
                            processingResultData = true;
                            parent.ProcessingResultData = true;
                        }
                    }
                    else
                    {
                        parent.ColumnNames.Add(field.Name.StringValue);
                    }
                }
            }

            await base.VisitFieldAsync(field, context);
            context.PopField();

            if (table != null)
                context.CurrentTables.Pop();
            if (join != null)
                context.CurrentJoins.Pop();
            if (childTable != null)
                context.CurrentTables.Pop();
            if (processingResultData)
                context.CurrentTables.First().ProcessingResultData = false;
        }
        protected async override ValueTask VisitArgumentAsync(GraphQLArgument argument, ISqlContext context)
        {
            using var cleanup = context.StartTableArgument(argument.Name.StringValue);
            context.PushArgument(argument.Name.StringValue);
            await base.VisitArgumentAsync(argument, context);
            context.PopArgument();
        }

        protected override async ValueTask VisitObjectValueAsync(GraphQLObjectValue objectValue, ISqlContext context)
        {
            var result = new Dictionary<string, object?>();
            context.FieldSetters.Push((key, value) => result[key] = value);
            await base.VisitObjectValueAsync(objectValue, context);
            context.FieldSetters.Pop();
            context.Setters.FirstOrDefault()?.Invoke(result);
            context.AddValue(result);
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
            context.AddValue(foundVariable?.Value);
            await base.VisitVariableAsync(variable, context);
        }

        protected async override ValueTask VisitFragmentSpreadAsync(GraphQLFragmentSpread spread, ISqlContext context)
        {
            var table = context.CurrentTables.FirstOrDefault();
            if (table != null)
                table.FragmentSpreads.Add(new FragmentSpread { FragmentName = spread.FragmentName.Name.StringValue });
            context.AddFragmentSpread(spread.FragmentName.Name.StringValue);
            await base.VisitFragmentSpreadAsync(spread, context);
        }

        protected async override ValueTask VisitFragmentDefinitionAsync(GraphQLFragmentDefinition fragmentDefinition, ISqlContext context)
        {
            var table = new TableSqlData()
            {
                TableName = fragmentDefinition.FragmentName.Name.StringValue,
                IsFragment = true,
                IncludeResult = true,
            };

            context.FragmentData.Add(table);
            context.CurrentTables.Push(table);
            context.PushFragment(fragmentDefinition.FragmentName.Name.StringValue, fragmentDefinition.FragmentName.Name.StringValue);
            await base.VisitFragmentDefinitionAsync(fragmentDefinition, context);
            context.CurrentTables.Pop();
            context.PopFragment();
        }

        protected override ValueTask VisitIntValueAsync(GraphQLIntValue value, ISqlContext context)
        {
            context.Set(Convert.ToInt32(value.Value.ToString()));
            context.AddValue(Convert.ToInt32(value.Value.ToString()));
            return base.VisitIntValueAsync(value, context);
        }
        protected override ValueTask VisitFloatValueAsync(GraphQLFloatValue value, ISqlContext context)
        {
            context.Set(Convert.ToDouble(value.Value.ToString()));
            context.AddValue(Convert.ToDouble(value.Value.ToString()));
            return base.VisitFloatValueAsync(value, context);
        }
        protected override async ValueTask VisitStringValueAsync(GraphQLStringValue stringValue, ISqlContext context)
        {
            context.Set(stringValue.Value.Span.ToString());
            context.AddValue(stringValue.Value.Span.ToString());
            await base.VisitStringValueAsync(stringValue, context);
        }

        protected override ValueTask VisitBooleanValueAsync(GraphQLBooleanValue value, ISqlContext context)
        {
            context.Set(value.BoolValue);
            context.AddValue(value.BoolValue);
            return base.VisitBooleanValueAsync(value, context);
        }
        protected override ValueTask VisitNullValueAsync(GraphQLNullValue value, ISqlContext context)
        {
            context.Set(null);
            context.AddValue(null);
            return base.VisitNullValueAsync(value, context);
        }
        protected override ValueTask VisitEnumValueAsync(GraphQLEnumValue value, ISqlContext context)
        {
            context.Set(value.Name.StringValue);
            context.AddValue(value.Name.StringValue);
            return base.VisitEnumValueAsync(value, context);
        }
        protected async override ValueTask VisitListValueAsync(GraphQLListValue listType, ISqlContext context)
        {
            var result = new List<object?>();
            context.Setters.Push(value => result.Add(value));
            await base.VisitListValueAsync(listType, context);
            context.Setters.Pop();
            context.Setters.FirstOrDefault()?.Invoke(result);
            context.AddValue(result);
        }

        protected async override ValueTask VisitDocumentAsync(GraphQLDocument document, ISqlContext context)
        {
            await base.VisitDocumentAsync(document, context);
            context.ReduceFragments();
            context.SyncFragments();
        }
    }
}
