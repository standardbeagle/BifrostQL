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
        sb.AppendLine("\tmanyToManyJoins: [dbManyToManyJoinSchema!]!");
        sb.AppendLine("\tcolumns: [dbColumnSchema!]!");
        sb.AppendLine("}");

        sb.AppendLine("type dbManyToManyJoinSchema {");
        sb.AppendLine("\tname: String!");
        sb.AppendLine("\ttargetTable: String!");
        sb.AppendLine("\tjunctionTable: String!");
        sb.AppendLine("\tjunctionTargetField: String!");
        sb.AppendLine("\tsourceColumnNames: [String!]!");
        sb.AppendLine("\tjunctionSourceColumnNames: [String!]!");
        sb.AppendLine("\tjunctionTargetColumnNames: [String!]!");
        sb.AppendLine("\ttargetColumnNames: [String!]!");
        sb.AppendLine("\thasPayload: Boolean!");
        sb.AppendLine("}");

        sb.AppendLine("type dbJoinSchema {");
        sb.AppendLine("\tname: String!");
        sb.AppendLine("\tfieldName: String!");
        sb.AppendLine("\tsourceColumnNames: [String!]!");
        sb.AppendLine("\tdestinationTable: String!");
        sb.AppendLine("\tdestinationColumnNames: [String!]!");
        sb.AppendLine("\tmetadata: [dbMetadataSchema!]!");
        // Polymorphic child links (e.g. a shared notes table joined via a
        // discriminator). Null on ordinary joins.
        sb.AppendLine("\tisPolymorphic: Boolean");
        sb.AppendLine("\tpolymorphicTypeColumn: String");
        sb.AppendLine("\tpolymorphicTypeValue: String");
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
        // min/max/step are strings: bounds may be numeric ("0.01") or dates ("2020-01-01")
        sb.AppendLine("\tmin: String");
        sb.AppendLine("\tmax: String");
        sb.AppendLine("\tstep: String");
        sb.AppendLine("\trequired: Boolean!");
        sb.AppendLine("\tprecision: Float");
        sb.AppendLine("\tscale: Float");
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
