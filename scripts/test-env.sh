#!/usr/bin/env bash
# Source this file to export the BIFROST_TEST_* connection strings that
# point at the local docker-compose.test.yml containers. Connection strings
# mirror .github/workflows/dotnet.yml exactly so local runs match CI.
#
#   source scripts/test-env.sh
#   dotnet test tests/BifrostQL.Integration.Test
#
# Bring containers up with:   docker compose -f docker-compose.test.yml up -d
# Tear them down with:        docker compose -f docker-compose.test.yml down

export BIFROST_TEST_SQLSERVER="Server=localhost,1433;Database=master;User Id=sa;Password=Bifrost!CI!Test1234;TrustServerCertificate=True;Encrypt=False"
export BIFROST_TEST_POSTGRES="Host=localhost;Port=5432;Database=bifrost_ci;Username=postgres;Password=bifrost_ci_test"
export BIFROST_TEST_MYSQL="Server=localhost;Port=3306;Database=bifrost_ci;User Id=root;Password=bifrost_ci_test;AllowPublicKeyRetrieval=True;SslMode=Preferred"
