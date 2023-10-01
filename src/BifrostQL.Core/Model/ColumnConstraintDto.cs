using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BifrostQL.Core.Model
{
    public sealed class ColumnConstraintDto
    {
        public string ConstraintCatalog { get; init; } = null!;
        public string ConstraintSchema { get; init; } = null!;
        public string ConstraintName { get; init; } = null!;
        public string TableCatalog { get; init; } = null!;
        public string TableSchema { get; init; } = null!;
        public string TableName { get; init; } = null!;
        public string ColumnName { get; init; } = null!;
        public string ConstraintType { get; init; } = null!;

        public static ColumnConstraintDto FromReader(IDataReader reader)
        {
            return new ColumnConstraintDto
            {
                ConstraintCatalog = (string)reader["CONSTRAINT_CATALOG"],
                ConstraintSchema = (string)reader["CONSTRAINT_SCHEMA"],
                ConstraintName = (string)reader["CONSTRAINT_NAME"],
                TableCatalog = (string)reader["TABLE_CATALOG"],
                TableSchema = (string)reader["TABLE_SCHEMA"],
                TableName = (string)reader["TABLE_NAME"],
                ColumnName = (string)reader["COLUMN_NAME"],
                ConstraintType = (string)reader["CONSTRAINT_TYPE"],
            };
        }

    }

}
