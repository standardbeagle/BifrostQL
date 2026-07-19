using System.Runtime.CompilerServices;

// Types physically relocated into BifrostQL.Abstractions but kept in their
// original BifrostQL.Core.* namespaces for back-compat. Each moved public type
// is re-exported from BifrostQL.Core here so that every existing consumer
// (BifrostQL.Server, BifrostQL.Mcp, BifrostQL.UI, the src/data/* dialects, and
// any third-party reference) recompiles and binds against BifrostQL.Core with
// ZERO source changes. A TypeForwardedTo on a type also forwards its nested
// types, so forwarding MetadataKeys covers MetadataKeys.Eav, .Security, etc.

// Model
[assembly: TypeForwardedTo(typeof(BifrostQL.Core.Model.MetadataKeys))]
[assembly: TypeForwardedTo(typeof(BifrostQL.Core.Model.ITypeMapper))]

// Resolvers
[assembly: TypeForwardedTo(typeof(BifrostQL.Core.Resolvers.BifrostExecutionError))]
[assembly: TypeForwardedTo(typeof(BifrostQL.Core.Resolvers.IBifrostFieldContext))]

// QueryModel — SQL-generation primitives
[assembly: TypeForwardedTo(typeof(BifrostQL.Core.QueryModel.ParameterizedSql))]
[assembly: TypeForwardedTo(typeof(BifrostQL.Core.QueryModel.SqlParameterInfo))]
[assembly: TypeForwardedTo(typeof(BifrostQL.Core.QueryModel.SqlParameterCollection))]
[assembly: TypeForwardedTo(typeof(BifrostQL.Core.QueryModel.SqlColumnKind))]
[assembly: TypeForwardedTo(typeof(BifrostQL.Core.QueryModel.SqlColumnDefinition))]
[assembly: TypeForwardedTo(typeof(BifrostQL.Core.QueryModel.SqlParameterNames))]
[assembly: TypeForwardedTo(typeof(BifrostQL.Core.QueryModel.FtsTerm))]
[assembly: TypeForwardedTo(typeof(BifrostQL.Core.QueryModel.FtsPredicateRequest))]
[assembly: TypeForwardedTo(typeof(BifrostQL.Core.QueryModel.FtsQueryParser))]
