# Query Organization in BifrostQL

BifrostQL organizes its GraphQL schema to provide a clean and intuitive way to interact with database tables. This organization helps manage complexity and keeps related functionality grouped together.

## Root Schema Organization

The root schema is organized into two main types:

```graphql
schema {
  query: database
  mutation: databaseInput
}
```

### Query Type (database)

The `database` type serves as the entry point for all read operations. Database tables are organized as fields under this type:

```graphql
type database {
  customers: [Customer!]
  orders: [Order!]
  products: [Product!]
  _dbSchema(graphQlName: String): [dbTableSchema!]!
}
```

Each table field supports filtering, sorting, and pagination through arguments:

```graphql
{
  customers(
    filter: CustomerFilterInput
    sort: [CustomerSortInput!]
    first: Int
    after: String
  ) {
    id
    name
    email
  }
}
```

### Mutation Type (databaseInput)

The `databaseInput` type groups all write operations. Each table has corresponding mutation fields:

```graphql
type databaseInput {
  customers: CustomerMutations
  orders: OrderMutations
  products: ProductMutations
}
```

## Table Types

Each database table has its own GraphQL type with fields for:
- Columns
- Relationships (single and multi)
- Dynamic joins
- Aggregations

For example:

```graphql
type Customer {
  id: ID!
  name: String!
  email: String
  # Relationships
  orders: [Order!]
  # Dynamic joins
  _join(table: String!): [JoinedTable!]
  # Aggregations
  _agg: CustomerAggregates
}
```

## Benefits of This Organization

1. **Clear Separation**: Separating read and write operations under distinct root types makes the API more intuitive.

2. **Grouped Functionality**: Related operations are grouped by table, making it easier to find and use specific functionality.

3. **Flexible Querying**: The schema supports complex operations through:
   - Dynamic joins between tables
   - Aggregation capabilities
   - Rich filtering and sorting options

4. **Maintainable Structure**: The organized structure makes it easier to:
   - Add new tables and operations
   - Modify existing functionality
   - Keep the codebase clean as it grows

## Example Queries

### Basic Query
```graphql
{
  database {
    customers {
      id
      name
      email
    }
  }
}
```

### Query with Relationships
```graphql
{
  database {
    orders {
      id
      orderDate
      customer {
        name
        email
      }
    }
  }
}
```

### Mutation Example
```graphql
mutation {
  databaseInput {
    customers {
      insert(input: {
        name: "John Doe"
        email: "john@example.com"
      }) {
        id
        name
        email
      }
    }
  }
}
```

This organization pattern helps maintain a clean and scalable GraphQL API while providing powerful database interaction capabilities.