using System.Text;

namespace BifrostQL.Core.Schema;

/// <summary>
/// Generates GraphQL schema types for database metadata introspection.
/// </summary>
public static class MetadataSchemaGenerator
{
    /// <summary>
    /// Generates all metadata-related schema types.
    /// </summary>
    public static string Generate()
    {
        var sb = new StringBuilder();

        sb.AppendLine("type dbTableSchema {");
        sb.AppendLine("\tschema: String!");
        sb.AppendLine("\tdbName: String!");
        sb.AppendLine("\tgraphQlName: String!");
        sb.AppendLine("\tprimaryKeys: [String!]");
        sb.AppendLine("\tlabelColumn: String!");
        sb.AppendLine("\tisEditable: Boolean!");
        sb.AppendLine("\tmetadata: [dbMetadataSchema!]!");
        sb.AppendLine("\tmultiJoins: [dbJoinSchema!]!");
        sb.AppendLine("\tsingleJoins: [dbJoinSchema!]!");
        sb.AppendLine("\tcolumns: [dbColumnSchema!]!");
        sb.AppendLine("}");

        sb.AppendLine("type dbJoinSchema {");
        sb.AppendLine("\tname: String!");
        sb.AppendLine("\tsourceColumnNames: [String!]!");
        sb.AppendLine("\tdestinationTable: String!");
        sb.AppendLine("\tdestinationColumnNames: [String!]!");
        sb.AppendLine("\tmetadata: [dbMetadataSchema!]!");
        sb.AppendLine("}");

        sb.AppendLine("type dbColumnSchema {");
        sb.AppendLine("\tdbName: String!");
        sb.AppendLine("\tgraphQlName: String!");
        sb.AppendLine("\tparamType: String!");
        sb.AppendLine("\tdbType: String!");
        sb.AppendLine("\tisNullable: Boolean!");
        sb.AppendLine("\tisReadOnly: Boolean!");
        sb.AppendLine("\tisPrimaryKey: Boolean!");
        sb.AppendLine("\tisUnique: Boolean!");
        sb.AppendLine("\tisIdentity: Boolean!");
        sb.AppendLine("\tisCreatedOnColumn: Boolean!");
        sb.AppendLine("\tisCreatedByColumn: Boolean!");
        sb.AppendLine("\tisUpdatedOnColumn: Boolean!");
        sb.AppendLine("\tisUpdatedByColumn: Boolean!");
        sb.AppendLine("\tisDeletedOnColumn: Boolean!");
        sb.AppendLine("\tisDeletedColumn: Boolean!");
        sb.AppendLine("\tmaxLength: Int");
        sb.AppendLine("\tminLength: Int");
        sb.AppendLine("\tmin: Float");
        sb.AppendLine("\tmax: Float");
        sb.AppendLine("\tstep: Float");
        sb.AppendLine("\tpattern: String");
        sb.AppendLine("\tpatternMessage: String");
        sb.AppendLine("\tinputType: String");
        sb.AppendLine("\tdefaultValue: String");
        sb.AppendLine("\tenumValues: [String!]");
        sb.AppendLine("\tenumLabels: [String!]");
        sb.AppendLine("\tmetadata: [dbMetadataSchema!]!");
        sb.AppendLine("}");

        sb.AppendLine("type dbMetadataSchema { key: String! value: String! }");

        sb.AppendLine("enum AggregateOperations {");
        sb.AppendLine(string.Join(',', Enum.GetNames(typeof(AggregateOperationType))));
        sb.AppendLine("}");

        return sb.ToString();
    }
}
