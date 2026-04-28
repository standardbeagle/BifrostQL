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
      dbType
      isPrimaryKey
      isIdentity
      isNullable
      isReadOnly
      maxLength
      minLength
      min
      max
      step
      pattern
      patternMessage
      inputType
      defaultValue
      enumValues
      enumLabels
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
