---
title: Query your WordPress database over GraphQL
published: false
description: Point BifrostQL at a stock WordPress MySQL database and get a GraphQL API with injected foreign keys and flattened postmeta — no plugin inside WordPress.
tags: wordpress, graphql, mysql, dotnet
canonical_url: https://dev.standardbeagle.com/BifrostQL/guides/wordpress/
---

Here is a query that runs against a stock WordPress database, with nothing installed inside WordPress itself:

```graphql
{
  wp_posts(filter: { post_status: { _eq: "publish" } }, limit: 3) {
    total
    data {
      iD
      post_title
      _meta
      wp_users { display_name user_email }
    }
  }
}
```

And the response, captured from the run described below:

```json
{"data":{"wp_posts":{"total":3,"data":[
  {"iD":1,"post_title":"Hello world!",
   "_meta":{"custom_field":"custom value","_thumbnail_id":"42"},
   "wp_users":{"display_name":"admin","user_email":"admin@example.com"}},
  {"iD":2,"post_title":"Sample Page",
   "_meta":{"_wp_page_template":"default"},
   "wp_users":{"display_name":"admin","user_email":"admin@example.com"}}
]}}}
```

Two things in that response are worth pausing on. The post's author arrived as a nested object, even though WordPress declares zero foreign keys in its DDL — I checked, and `information_schema` reports `fk_count = 0` for this database. And `_meta` came back as a single JSON object instead of a `wp_postmeta` result set you have to pivot yourself.

**How this was run.** WordPress 7.0.4 installed by `wp-cli` into a MySQL 8.4 container, then `dotnet run --project src/BifrostQL.Host` bound to `127.0.0.1:5302`. Every GraphQL response quoted here is copied from that session on 2026-08-17. Where something is described but not exercised, I say so explicitly.

## Getting connected

BifrostQL is a .NET library that reads a database schema and publishes it as GraphQL. For WordPress you need a connection string and a provider:

```json
{
  "ConnectionStrings": {
    "bifrost": "Server=127.0.0.1;Port=13306;Database=wordpress;Uid=wp;Pwd=xxx;"
  },
  "BifrostQL": {
    "Provider": "mysql",
    "Path": "/graphql",
    "Playground": "/edit"
  }
}
```

Set `Provider` explicitly. When the key is absent, BifrostQL infers the provider from the connection string, and that inference is narrow for MySQL: `DbConnFactory.IsMySql` matches on `Uid=`, or on `Pwd=` without `User Id=`, or on `SslMode=` together with `Port=3306`. A conventional string like `Server=localhost;Database=wordpress;User Id=wp;Password=xxx;` matches the SQL Server test first, and you get T-SQL generated against MySQL. Accepted values for MySQL are `mysql` and `mariadb`. The client library underneath is MySqlConnector, and the dialect quotes identifiers with backticks and concatenates with `CONCAT()`.

## What detection actually does

`WordPressDetector` looks for three signature tables under a common prefix — `{prefix}users`, `{prefix}posts`, `{prefix}options`. All three must be present. The prefix is discovered from the schema rather than assumed, so `mysite_` works exactly as `wp_` does; the character before the signature name must be an underscore.

Once a prefix group matches, the detector injects ten synthetic foreign keys:

| Child column | Parent |
|---|---|
| `posts.post_author` | `users.ID` |
| `posts.post_parent` | `posts.ID` |
| `postmeta.post_id` | `posts.ID` |
| `usermeta.user_id` | `users.ID` |
| `comments.comment_post_ID` | `posts.ID` |
| `comments.user_id` | `users.ID` |
| `commentmeta.comment_id` | `comments.comment_ID` |
| `termmeta.term_id` | `terms.term_id` |
| `term_taxonomy.term_id` | `terms.term_id` |
| `term_relationships.term_taxonomy_id` | `term_taxonomy.term_taxonomy_id` |

Each pair is only injected when both tables exist in the same prefix group, which is what makes the multisite behaviour predictable and also what limits it — more on that below.

The relationship field on a type is named after the related table, so `wp_posts` carries a `wp_users` field and `wp_users` carries a `wp_posts` field. Three levels compose the way you would expect:

```graphql
{
  wp_term_relationships(limit: 3) {
    data {
      object_id
      wp_term_taxonomy { taxonomy count wp_terms { name slug } }
    }
  }
}
```

```json
{"object_id":1,"wp_term_taxonomy":{"taxonomy":"category","count":2,
 "wp_terms":{"name":"Uncategorized","slug":"uncategorized"}}}
```

The detector also attaches table labels — `wp_posts` gets "Posts", `wp_postmeta` gets "Post Meta", and so on for the twelve core tables — and hides four Action Scheduler tables (`actionscheduler_actions`, `actionscheduler_claims`, `actionscheduler_groups`, `actionscheduler_logs`) by writing `visibility: hidden`. Those are matched by exact name against the group prefix, not by wildcard, so a plugin table named `wp_actionscheduler_something_else` stays visible. My test install had no Action Scheduler tables, so that path is described from the source rather than observed.

## The `_meta` field

WordPress meta tables are entity-attribute-value stores: one row per key, joined back to a parent by id. BifrostQL configures four of them as EAV children — `postmeta`, `usermeta`, `termmeta`, `commentmeta` — and adds a computed `_meta` column of type JSON to each parent. Querying users shows what that collapses:

```json
{"iD":1,"user_login":"admin","display_name":"admin",
 "_meta":{"nickname":"admin","rich_editing":"true","admin_color":"modern",
          "wp_capabilities":"a:1:{s:13:\"administrator\";b:1;}",
          "wp_user_level":"10","show_welcome_panel":"1"}}
```

Two operational details from the source. `_meta` is read-only and issues one query per parent row, so it is an N+1 by construction — fine for a page of ten posts, expensive for a thousand. And a parent table with a composite primary key returns null rather than a partial object, which does not affect core WordPress tables but can affect custom ones.

## The naming rule that will trip you

Column names pass through a sanitizer that lowercases the first character. `post_author` survives unchanged, but `ID` becomes `iD`, and the server is blunt about it:

```json
{"errors":[{"message":"Cannot query field 'ID' on type 'wp_posts'. Did you mean 'iD'?"}]}
```

Table names are not transformed, so `wp_posts` stays `wp_posts` for both the root query field and the object type. Each table also gets `wp_postsAggregate` and `wp_postsPivot` siblings, and the paged result carries `data`, `total`, `offset`, and `limit`.

## PHP serialized values come back as stored

WordPress writes structured option and meta values using PHP's `serialize()` format. The detector marks those columns — `options.option_value` plus the four `meta_value` columns — with `type: php_serialized`, and the repository ships a `PhpSerializer` with a working `ToJson`. What it does not yet ship is a call site: `PhpSerializer` has no consumer in `src/` outside its own file, and the query result confirms it. Asking for `active_plugins` returns the raw string:

```json
{"option_name":"active_plugins",
 "option_value":"a:2:{i:0;s:19:\"akismet/akismet.php\";i:1;s:33:\"classic-editor/classic-editor.php\";}"}
```

The same raw form shows up as `wp_capabilities` in the user `_meta` above. So plan to decode these client-side today. The annotation is on the column, so a consumer can find the affected columns from the schema metadata instead of hard-coding a list, and the remaining work is wiring the existing serializer into the read path.

## Multisite, with a caveat

Each prefix becomes its own group, and injected foreign keys are confined to a group — `wp_2_posts` links to `wp_2_postmeta`, never to `wp_3_postmeta`. The caveat is in the signature check: a group is only recognised when that prefix has its own `users`, `posts`, and `options` tables. Real multisite shares one global `wp_users` and creates only per-site content tables, so a `wp_2_` prefix fails the three-table check as written. Group membership is a `StartsWith` test, so `wp_2_posts` also falls inside the `wp_` group. Treat multisite as partially supported and verify against your own schema before relying on it. I ran single-site only.

## Overrides

Both controls are database-scoped metadata rules in the `BifrostQL:Metadata` array, not standalone settings:

```json
"Metadata": [
  "* { app-schema: wordpress }"
]
```

`app-schema: wordpress` forces the detector when renamed tables defeat the signature check. `auto-detect-app: disabled` turns detection off entirely and leaves you with plain BifrostQL behaviour. The detector also records `detected-app`, `detection-confidence`, and `prefix-groups` on the model, which is the fastest way to see what it concluded.

## What this is good for

Reporting and export are the obvious wins: a query that joins posts to authors to taxonomy in one round trip, against the database you already have, with no PHP in the path. The gaps are worth knowing before you commit — decode serialized values yourself, budget for the `_meta` N+1, and check multisite against your own prefixes. Everything else in this article ran end to end against a stock install.
