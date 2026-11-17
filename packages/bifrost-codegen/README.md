# BifrostQL Codegen

`bifrostql-codegen` reads a BifrostQL `.proto` schema and emits one TypeScript
file per message and enum plus an `index.ts` barrel:

```
bifrostql-codegen --proto-file schema.proto --out packages/@bifrostql/types/src/generated
```

The interfaces describe rows as they arrive in JSON. `int64` maps to `string`
(the server's `BigInt` scalar writes every value as a decimal string, so a key
past 2^53 is never rounded by `JSON.parse`), and `oneof` members are optional
properties.

## Grant catalogue

Add `--grants <response.json>` to also emit `grants.ts` beside the message
files — `export type Grant = 'rates.view_cost' | 'team.manage'` and
`export const GRANTS: readonly Grant[]` — and re-export `Grant` and `GRANTS`
from the barrel, so a misspelt or retired capability is a compile error in
`@bifrostql/types` consumers:

```
bifrostql-codegen --proto-file schema.proto --grants response.json --out generated
```

`--grants` always rides a proto source (`--proto-file` or `--endpoint`); on
its own it exits 2 naming the flag. The response file is a saved GraphQL
response carrying `_policyGrants` (and `_grants`), for example the output of
`{ _policyGrants _grants }`. An empty catalogue emits `type Grant = never`
and an empty `GRANTS` array with no barrel line.

The response must be the full projection: query with administrator
credentials or a dedicated codegen identity. When `_policyGrants` is
non-empty but `_grants` lacks `admin`, the CLI warns on stderr that the
response may be narrowed and still exits 0.
