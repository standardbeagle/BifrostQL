---
title: "GraphQL API for a WordPress Database"
description: "Serve a WordPress MySQL database as a GraphQL API, with schema auto-detection, ten injected foreign keys, postmeta flattening, and friendly table labels."
---

BifrostQL auto-detects WordPress databases and configures the GraphQL API to match WordPress's data model. Connect to the database and the API is ready — no mapping files, no manual FK definitions.

## Quick start

Point BifrostQL at your WordPress database, and set the `Provider` explicitly:

```json
{
  "ConnectionStrings": {
    "bifrost": "Server=localhost;Database=wordpress;Uid=wp_user;Pwd=xxx;"
  },
  "BifrostQL": {
    "Provider": "mysql"
  }
}
```

Always set `Provider` (`mysql` or `mariadb`) for a WordPress database. When the key is absent, BifrostQL infers the provider from the connection string, and that inference is narrow for MySQL: it matches on `Uid=`, on `Pwd=` without `User Id=`, or on `SslMode=` together with `Port=3306`. A conventional string like `Server=localhost;Database=wordpress;User Id=wp;Password=xxx;` is inferred as SQL Server, and you get T-SQL generated against MySQL.

BifrostQL detects the WordPress schema, applies the configuration, and publishes the API. Open the GraphQL playground and query immediately.

## What auto-detection does

### Prefix detection

BifrostQL identifies the WordPress prefix by scanning for `{prefix}users`, `{prefix}posts`, and `{prefix}options`. The standard prefix is `wp_`, but any custom prefix is detected automatically.

### Foreign key injection

WordPress doesn't declare foreign keys in its DDL. BifrostQL injects the following ten relationships:

| Source column | Target table | Target column |
|--------------|-------------|--------------|
| `wp_posts.post_author` | `wp_users` | `ID` |
| `wp_posts.post_parent` | `wp_posts` | `ID` |
| `wp_postmeta.post_id` | `wp_posts` | `ID` |
| `wp_usermeta.user_id` | `wp_users` | `ID` |
| `wp_comments.comment_post_ID` | `wp_posts` | `ID` |
| `wp_comments.user_id` | `wp_users` | `ID` |
| `wp_commentmeta.comment_id` | `wp_comments` | `comment_ID` |
| `wp_termmeta.term_id` | `wp_terms` | `term_id` |
| `wp_term_taxonomy.term_id` | `wp_terms` | `term_id` |
| `wp_term_relationships.term_taxonomy_id` | `wp_term_taxonomy` | `term_taxonomy_id` |

These injected FKs enable join navigation in GraphQL without any manual configuration.

### Hidden tables

Four Action Scheduler tables are hidden automatically: `wp_actionscheduler_actions`, `wp_actionscheduler_claims`, `wp_actionscheduler_groups`, and `wp_actionscheduler_logs`. They are matched by exact name against the detected prefix, not by wildcard, so a plugin table named `wp_actionscheduler_something_else` stays visible.

Hidden tables don't appear in the GraphQL schema but remain accessible in the database.

### Friendly labels

The twelve core tables receive human-readable labels: `wp_posts` becomes "Posts", `wp_postmeta` becomes "Post Meta", `wp_term_taxonomy` becomes "Term Taxonomy", and so on. Columns are not labeled — column names appear as-is (subject to the naming rule below).

### Column naming: `ID` becomes `iD`

GraphQL field names for columns pass through a sanitizer that lowercases the first character. `post_title` survives unchanged, but the WordPress `ID` column becomes `iD` in every query and response. Querying `ID` returns an error: `Cannot query field 'ID' on type 'wp_posts'. Did you mean 'iD'?` Table names such as `wp_posts` start lowercase already and are unaffected.

## Multisite support

WordPress multisite uses a shared `wp_users` table with per-site content tables under distinct prefixes:

- `wp_` — site 1 (and shared tables like `wp_users`)
- `wp_2_` — site 2
- `wp_3_` — site 3

BifrostQL detects each prefix as a separate group. Auto-links are scoped within each group, so `wp_2_posts` links to `wp_2_postmeta` but not to `wp_3_postmeta`.

Cross-group relationships work through the injected FKs. `wp_2_posts.post_author` correctly joins to the shared `wp_users.ID` table because explicit FKs cross prefix boundaries.

## EAV meta flattening

WordPress stores extensible data in Entity-Attribute-Value (EAV) meta tables: `wp_postmeta`, `wp_usermeta`, `wp_commentmeta`, and `wp_termmeta`. Each row is a key-value pair linked to a parent record.

BifrostQL flattens these into a `_meta` field on the parent type. Instead of querying a separate meta table and pivoting the results yourself, you get a JSON object with all meta keys as properties:

```graphql
{
  wp_posts(limit: 5) {
    data {
      iD
      post_title
      _meta
    }
  }
}
```

Returns:

```json
{
  "data": {
    "wp_posts": {
      "data": [
        {
          "iD": 1,
          "post_title": "Hello world!",
          "_meta": {
            "_edit_last": "1",
            "_thumbnail_id": "42",
            "custom_field": "custom value"
          }
        }
      ]
    }
  }
}
```

The `_meta` field is available on posts, users, comments, and terms.

## PHP serialized values are returned as stored

WordPress stores structured data using PHP's `serialize()` format in meta values and options. BifrostQL returns these values **as stored** — no deserialization happens on the read path today:

```graphql
{
  wp_options(filter: { option_name: { _eq: "active_plugins" } }) {
    data {
      option_name
      option_value
    }
  }
}
```

Returns the raw serialized string:

```json
{
  "option_name": "active_plugins",
  "option_value": "a:2:{i:0;s:19:\"akismet/akismet.php\";i:1;s:33:\"classic-editor/classic-editor.php\";}"
}
```

Plan to decode these client-side. The detector does mark the affected columns — `wp_options.option_value` plus the `meta_value` column of the four meta tables — with `type: php_serialized` and `format: php`, so a consumer can find them from the schema metadata instead of hard-coding a list. The repository ships a `PhpSerializer` with a working `ToJson`, but it is not yet wired into the read path.

## Example queries

### Posts with author

```graphql
{
  wp_posts(filter: { post_status: { _eq: "publish" } }, limit: 10) {
    data {
      iD
      post_title
      post_date
      wp_users {
        display_name
        user_email
      }
    }
  }
}
```

### Posts with meta rows

```graphql
{
  wp_posts(filter: { post_type: { _eq: "post" } }, limit: 5) {
    data {
      iD
      post_title
      _meta
      wp_postmeta(filter: { meta_key: { _eq: "_thumbnail_id" } }) {
        meta_key
        meta_value
      }
    }
  }
}
```

### Term taxonomy with terms

```graphql
{
  wp_term_relationships(limit: 10) {
    data {
      object_id
      wp_term_taxonomy {
        taxonomy
        wp_terms {
          name
          slug
        }
      }
    }
  }
}
```

### Users with meta

```graphql
{
  wp_users(limit: 10) {
    data {
      iD
      user_login
      display_name
      _meta
    }
  }
}
```

## Manual override

### Force WordPress detection

If auto-detection doesn't trigger (e.g., tables were renamed), force it:

```
app-schema: wordpress
```

### Disable detection entirely

To use standard BifrostQL behavior without WordPress-specific configuration:

```
auto-detect-app: disabled
```

### Custom prefixes

No special configuration needed. BifrostQL detects any prefix, not just `wp_`. If your WordPress installation uses `mysite_` as the prefix, detection works the same way — it finds `mysite_users`, `mysite_posts`, and `mysite_options`, then applies the configuration with `mysite_` as the prefix.

## See also

- [Application Schema Detection](/docs/concepts/app-schema-detection) — How auto-detection works
- [App Schema Detection Framework](/docs/app-schema-detection) — Framework architecture and API
- [Creating Custom Detectors](/docs/creating-custom-detectors) — Build your own detectors
- [WordPress Schema Bundle](/docs/wordpress-schema-bundle) — Complete bundle documentation
