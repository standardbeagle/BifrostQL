export const GET_SCHEMA = `{
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

export const GET_DB_SCHEMA = `
query dbSchema {
  _dbSchema {
    dbName
    graphQlName
    labelColumn
    primaryKeys
    isEditable
    metadata {
      key
      value
    }
    columns {
      dbName
      graphQlName
      paramType
      isPrimaryKey
      isIdentity
      isNullable
      isReadOnly
      metadata {
        key
        value
      }
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
