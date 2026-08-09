#!/bin/bash
# Captures a verbatim transcript of three different real clients reading the SAME
# BifrostQL-fronted SQLite database over three different wire protocols. Each
# command is echoed exactly as run, followed by its real output.
export PGPASSWORD=demo-secret-local
PGC="host=127.0.0.1 port=55432 user=demo dbname=bifrost sslmode=require"

run() {
  echo "\$ $1"
  eval "$2" 2>&1
  echo
}

echo "### One SQLite database. Three wire protocols. No per-protocol code."
echo
echo "--- GraphQL (HTTP) ---"
run "curl -s localhost:5099/graphql -d '{\"query\":\"{products(limit:3,sort:[price_desc]){data{name price}}}\"}'" \
    "curl -s -X POST http://127.0.0.1:5099/graphql -H 'content-type: application/json' -d '{\"query\":\"{ products(limit:3, sort:[price_desc]) { data { name price } } }\"}'"

echo "--- PostgreSQL wire protocol (psql, TLS + SCRAM-SHA-256) ---"
run "psql -c 'SELECT name, price FROM products ORDER BY price DESC LIMIT 3;'" \
    "psql \"\$PGC\" -c 'SELECT name, price FROM products ORDER BY price DESC LIMIT 3;'"
run "psql -c '\\dt'" "psql \"\$PGC\" -c '\\dt'"

echo "--- Redis wire protocol (redis-cli) ---"
run "redis-cli -p 6399 --user demo --pass ****** HGETALL products:1" \
    "redis-cli -h 127.0.0.1 -p 6399 --user demo --pass demo-secret-local --no-auth-warning HGETALL products:1 | head -12"
run "redis-cli -p 6399 SCAN 0 MATCH 'categories:*' COUNT 5" \
    "redis-cli -h 127.0.0.1 -p 6399 --user demo --pass demo-secret-local --no-auth-warning SCAN 0 MATCH 'categories:*' COUNT 5"

echo "--- OData v4 (what Excel and Power BI speak) ---"
run "curl -s -u demo:****** 'localhost:5099/odata/products?\$filter=price gt 1800&\$select=name,price&\$orderby=price desc'" \
    "curl -s -u demo:demo-secret-local \"http://127.0.0.1:5099/odata/products?\\\$filter=price%20gt%201800&\\\$select=name,price&\\\$orderby=price%20desc&\\\$top=3\" | python3 -m json.tool | head -16"

echo "--- Same guards on every door ---"
run "redis-cli -p 6399 HGETALL products:1      # no AUTH" \
    "redis-cli -h 127.0.0.1 -p 6399 --no-auth-warning HGETALL products:1"
run "redis-cli -p 6399 --user demo --pass ****** SET products:1 hacked" \
    "redis-cli -h 127.0.0.1 -p 6399 --user demo --pass demo-secret-local --no-auth-warning SET products:1 hacked"
run "psql -c 'DROP TABLE products;'" \
    "psql \"\$PGC\" -c 'DROP TABLE products;'"
run "curl -s 'localhost:5099/odata/\$metadata'   # no credentials" \
    "curl -s 'http://127.0.0.1:5099/odata/\\\$metadata'"
