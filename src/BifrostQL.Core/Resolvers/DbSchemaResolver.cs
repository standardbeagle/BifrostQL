using GraphQL.Resolvers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BifrostQL.Core.Model;
using GraphQL;
using System.Data.Common;
using BifrostQL.Core.Schema;

namespace BifrostQL.Core.Resolvers
{
    public interface IDbSchemaResolver : IFieldResolver
    {

    }

    public class DbSchemaResolver : IDbSchemaResolver
    {
        private readonly IDbModel _dbModel;
        public DbSchemaResolver(IDbModel dbModel)
        {
            _dbModel = dbModel;
        }

        public ValueTask<object?> ResolveAsync(IResolveFieldContext context)
        {
            var tableName = context.GetArgument<string?>("graphQlName");
            return ValueTask.FromResult<object?>(
                        _dbModel.Tables.Where(t => tableName == null || t.GraphQlName == tableName).Select(t => new
                        {
                            Schema = t.TableSchema,
                            t.DbName,
                            t.GraphQlName,
                            labelColumn = t.Columns.First().GraphQlName,
                            primaryKeys = t.Columns.Where(c => c.IsPrimaryKey == true).Select(pk => pk.GraphQlName),
                            isEditable = t.Columns.Any(c => c.IsPrimaryKey == true),
                            columns = t.Columns.Select(c => new
                            {
                                dbName = c.DbName, 
                                graphQlName = c.GraphQlName,
                                paramType = SchemaGenerator.GetGraphQlTypeName(c.DataType, c.IsNullable),
                                dbType = c.DataType,
                                isNullable = c.IsNullable,
                                isPrimaryKey = c.IsPrimaryKey,
                                isIdentity = c.IsIdentity,
                                isCreatedOnColumn = c.IsCreatedOnColumn,
                                isCreatedByColumn = c.IsCreatedByColumn,
                                isUpdatedOnColumn = c.IsUpdatedOnColumn,
                                isUpdatedByColumn = c.IsUpdatedByColumn,
                                isReadOnly = c.IsPrimaryKey || c.IsIdentity || c.IsCreatedOnColumn || c.IsCreatedByColumn || c.IsUpdatedOnColumn || c.IsUpdatedByColumn,
                                isDeletedOnColumn = false,
                                isDeletedColumn = false,
                            }),
                            multiJoins = t.MultiLinks.Values.Select(j => new
                            {
                                name = j.Name, 
                                sourceColumnNames = new [] {j.ParentId.GraphQlName},
                                destinationTable = j.ChildTable.GraphQlName,
                                destinationColumnNames = new[] {j.ChildId.GraphQlName},
                            }),
                            singleJoins = t.SingleLinks.Values.Select(j => new
                            {
                                name = j.Name,
                                sourceColumnNames = new[] { j.ChildId.GraphQlName },
                                destinationTable = j.ParentTable.GraphQlName,
                                destinationColumnNames = new[] { j.ParentId.GraphQlName },
                            })
                        })
                );
        }
    }
}
