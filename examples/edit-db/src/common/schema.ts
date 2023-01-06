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
