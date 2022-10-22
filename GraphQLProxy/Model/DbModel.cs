using System.Data;

namespace GraphQLProxy.Model
{

    public interface IDbModel
    {
        IReadOnlyCollection<TableDto> Tables { get; set; }
    }
    public sealed class DbModel : IDbModel
    {
        public IReadOnlyCollection<TableDto> Tables { get; set; } = null!;
    }

    public sealed class TableDto
    {
        public string TableName { get; set; } = null!;
        public string TableSchema { get; set; } = null!;
        public string TableType { get; set; } = null!;
        public IReadOnlyCollection<ColumnDto> Columns { get; set; } = null!;

        public static TableDto FromReader(IDataReader reader, IReadOnlyCollection<ColumnDto>? columns = null)
        {
            return new TableDto
            {
                TableName = (string)reader["TABLE_NAME"],
                TableSchema = (string)reader["TABLE_SCHEMA"],
                TableType = (string)reader["TABLE_TYPE"],
                Columns = columns ?? Array.Empty<ColumnDto>()
            };
        }
    }

    public sealed class ColumnDto
    {
        public string TableSchema { get; set; } = null!;
        public string TableName { get; set; } = null!;
        public string ColumnName { get; set; } = null!;
        public string DataType { get; set; } = null!;
        public bool IsNullable { get; set; }
        public int OrdinalPosition { get; set; }

        public static ColumnDto FromReader(IDataReader reader)
        {
            return new ColumnDto
            {
                TableSchema = (string)reader["TABLE_SCHEMA"],
                TableName = (string)reader["TABLE_NAME"],
                ColumnName = (string)reader["COLUMN_NAME"],
                DataType = (string)reader["DATA_TYPE"],
                //IsNullable = (bool)reader["IS_NULLABLE"],
                OrdinalPosition = (int)reader["ORDINAL_POSITION"],
            };
        }
    }
}
