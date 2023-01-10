# HandyQL
The easist way to publish your SQL database as a GraphQL API

### Overview
HandyQL is built to be the easiest way to publish a SQL database. The simplest setup will get you up and running with a single connection string. Unlike other approaches HandyQL builds its schema directly from the database schema itself. When you add a table or a column, HandyQL automatically adds the corresponsing field in the right place with the right type and validation requirements.

Joins between tables can be specified dynamically in your graphQL queries using __join fields added automatically to every table. Filters and paging fields are added automatically as well using directus syntax.

HandyQL generates optimized SQL batch queries to provide lightning fast responses, and because the SQL is generated from the query only the tables and fields you need are queried.

Mutations are generated for inserts, updates, upserts, and deletes. HandyQL reads the primary key data for every table and makes the appropriate choice matching your javascript fields appropriately based on names.

### Features
 - [x] Dynamically read schema from SQL Server databases
 - [x] Support dynamic table joins using single column
 - [x] Support single object mutations
 - [x] Configuration to filter exported tables using regex syntax on schema and table names
 - [x] Support dynamic parent/single object joins
 - [x] Use column names to infer simple joins between tables
 - [ ] Add Single join ref to filters
 - [ ] Add support for integrating oauth2 authentication services
 - [ ] Add configuration to automatically fill audit type columns
 - [ ] Add configuration to automatically generate filters based on userid/column mappings 
 - [ ] Add multi column filter options
 - [ ] Add multi colmun joins using relations
 - [ ] Add simplified many-to-many join syntax
 - [ ] Use foreign keys to infer simple joins between tables
 - [ ] Add support for soft deletes
 - [ ] Add support to use database auth
 - [ ] Add aggregation operators
 - [ ] Stored procedures
 - [ ] Raw SQL queries, maybe add raw filter arguments as well
 - [ ] PostreSQL
 - [ ] MySQL
 - [ ] SqlLite
 - [ ] Add Enum designation to lookup tables
 - [ ] Switch to direct graphQL schema generation
 - [ ] Try out pivot tables for kicks

 ### Why HandyQL
 At StandardBeagle we've built many prototypes, and like using GraphQL. Over time we've started to use applications like [hasura](https://hasura.io/) to roll out apis for prototypes quickly. We wanted to try and build something similar, but that put more power in the hands of the front end developer. 

 If we could we wanted to see if we could shape and filter our data directly from the queries without having to configure anything through a UI. Ideally something we could spin up directly in docker with nothing but a configuration file. 

 Performance is a concern for all our applications, which is why we wanted to build it in .net. 