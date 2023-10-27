# BifrostQL
Is a magical bridge connecting your front end code to your SQL datasources. 

### Overview
BifrostQL is built to be the easiest way to publish a SQL database as a GraphQL api. The simplest setup will get you up and running with a single connection string. Unlike other approaches BifrostQL builds its schema directly from the database schema itself. When you add a table or a column, BifrostQL automatically adds the corresponsing field in the right place with the right type and validation requirements.

Joins between tables can be specified dynamically in your graphQL queries using __join fields added automatically to every table. Filters and paging fields are added automatically as well using directus syntax.

BifrostQL generates optimized SQL batch queries to provide lightning fast responses, and because the SQL is generated from the query only the tables and fields you need are queried.

Mutations are generated for inserts, updates, upserts, and deletes. BifrostQL reads the primary key data for every table and makes the appropriate choice matching your javascript fields appropriately based on names.

### Features
 - [x] Dynamically read schema from SQL Server databases
 - [x] Support dynamic table joins using single column
 - [x] Support single object mutations
 - [x] Configuration to filter exported tables using regex syntax on schema and table names
 - [x] Support dynamic parent/single object joins
 - [x] Use column names to infer simple joins between tables
 - [x] Add Single join ref to filters
 - [x] Add support for integrating oauth2 authentication services
 - [x] Switch to direct graphQL schema generation
 - [x] Add multi column filter options
 - [x] Add multi colmun joins using relations
 - [x] Add aggregation operators
 - [ ] Add configuration to automatically fill audit type columns
 - [ ] Add support for soft deletes
 - [ ] Add GraphQLJSON support
 - [ ] Add Generic table data type
 - [ ] Stored procedures
 - [ ] Raw SQL queries, maybe add raw filter arguments as well
 - [ ] Add info/status endpoint update response headers with bifrost as the server
 - [ ] SqlLite
 - [ ] PostreSQL
 - [ ] MySQL
 - [ ] Add Enum designation to lookup tables
 - [ ] Batched operation commands
 - [ ] Inferred batch object tree sync
 - [ ] Add configuration to automatically generate filters based on userid/column mappings 
 - [ ] Add simplified many-to-many join syntax
 - [ ] Use foreign keys to infer simple joins between tables
 - [ ] Support multiple schemas as prefixes
 - [ ] Support multiple schemas as fields, and joins between schemas
 - [ ] Multiple databases as endpoints
 - [ ] Multiple databases as fields
 - [ ] Add support to use database auth
 - [ ] Try out pivot tables for kicks
 - [ ] Multiple configuration sources, config file, database table, config database
 - [ ] Configuration file builder/importer

 ### Why BifrostQL
 At StandardBeagle we've built many prototypes, and like using GraphQL. Over time we've started to use applications like [hasura](https://hasura.io/) to roll out apis for prototypes quickly. We wanted to try and build something similar, but that put more power in the hands of the front end developer. 

 If we could we wanted to see if we could shape and filter our data directly from the queries without having to configure anything through a UI. Ideally something we could spin up directly in docker with nothing but a configuration file. 

 Performance is a concern for all our applications, which is why we wanted to build it in .net. 