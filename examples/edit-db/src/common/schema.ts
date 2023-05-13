import { gql } from "@apollo/client";

export const GET_SCHEMA = gql`{
    schema: __schema {
      queryType {
        fields {
          name
          type {
            fields {
              name
              type {
                ofType {
                  fields {
                    name
                    type {
                      kind
                      ofType {
                        name
                      }
                    }
                  }
                }
              }
            }
          }
        }
      }
    }
  }
  `;

export const GET_DB_SCHEMA = gql`
query dbSchema {
  _dbSchema {
    dbName
    graphQlName
    labelColumn
    primaryKeys
    isEditable
    columns {
      dbName
      graphQlName
      paramType
      isPrimaryKey
      isIdentity
      isNullable
      isReadOnly
      isUpdatedOnColumn
      isUpdatedByColumn
      isCreatedOnColumn
      isCreatedByColumn
      isDeletedColumn
      isDeletedOnColumn
    }
    multiJoins {
      name
      sourceColumnNames
      destinationTable
      destinationColumnNames
    }
    singleJoins {
      name
      sourceColumnNames
      destinationTable
      destinationColumnNames
    }
  }
}`;
