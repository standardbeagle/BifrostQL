# BifrostQL Codegen

Generate the grant catalogue with `bifrostql-codegen --grants response.json --out generated`.
The response must be the full projection: query with administrator credentials or a dedicated
codegen identity. A narrowed response is warned about when `_policyGrants` is non-empty but
`_grants` lacks `admin`.
