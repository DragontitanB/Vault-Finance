[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$previousConnection = [Environment]::GetEnvironmentVariable('ConnectionStrings__DefaultConnection')
Push-Location $projectRoot

try {
    $connectionString = $previousConnection
    if ([string]::IsNullOrWhiteSpace($connectionString)) {
        try {
            [xml]$project = [IO.File]::ReadAllText((Join-Path $projectRoot 'backend/src/VaultFinance.Api/VaultFinance.Api.csproj'))
            $secretId = $project.SelectSingleNode('//UserSecretsId').InnerText
            $secretPath = Join-Path $env:APPDATA ("Microsoft/UserSecrets/" + $secretId + "/secrets.json")
            $values = [IO.File]::ReadAllText($secretPath) | ConvertFrom-Json
            $flat = $values.PSObject.Properties['ConnectionStrings:DefaultConnection']
            if ($null -ne $flat) {
                $connectionString = [string]$flat.Value
            } else {
                $nested = $values.PSObject.Properties['ConnectionStrings']
                if ($null -ne $nested) {
                    $entry = $nested.Value.PSObject.Properties['DefaultConnection']
                    if ($null -ne $entry) { $connectionString = [string]$entry.Value }
                }
            }
        } catch {
            throw 'Could not read API User Secrets. No configuration value has been printed.'
        }
    }

    if ([string]::IsNullOrWhiteSpace($connectionString)) {
        throw 'DefaultConnection is required in API User Secrets or environment configuration.'
    }

    try {
        $target = New-Object System.Data.SqlClient.SqlConnectionStringBuilder
        $target.set_ConnectionString($connectionString)
    } catch {
        throw 'DefaultConnection could not be parsed. No configuration value has been printed.'
    }

    if ($target.InitialCatalog -cne 'VaultFinanceDb' -or -not $target.IntegratedSecurity -or
        $target.DataSource -notin @('localhost\SQLEXPRESS', 'DRAGON\SQLEXPRESS', '.\SQLEXPRESS')) {
        throw 'This local migration script only permits VaultFinanceDb on local SQLEXPRESS using Windows authentication.'
    }

    $master = New-Object System.Data.SqlClient.SqlConnectionStringBuilder
    $master.set_ConnectionString($connectionString)
    $master.set_InitialCatalog('master')
    $catalogConnection = New-Object System.Data.SqlClient.SqlConnection($master.ConnectionString)
    try {
        $catalogConnection.Open()
        $query = $catalogConnection.CreateCommand()
        $query.CommandText = "SELECT COUNT(*) FROM sys.databases WHERE name = N'VaultFinanceDb';"
        $exists = [int]$query.ExecuteScalar() -eq 1
    } finally {
        $catalogConnection.Dispose()
    }

    $expectedTables = @(
        'Accounts', 'AspNetRoles', 'AspNetRoleClaims', 'AspNetUsers',
        'AspNetUserClaims', 'AspNetUserLogins', 'AspNetUserRoles', 'AspNetUserTokens',
        '__EFMigrationsHistory'
    )
    $migration = @(Get-ChildItem -LiteralPath 'backend/src/VaultFinance.Infrastructure/Persistence/Migrations' -Filter '*_InitialPersistence.cs')
    if ($migration.Count -ne 1) { throw 'Expected exactly one reviewed initial migration.' }
    $migrationId = $migration[0].BaseName

    if ($exists) {
        $databaseConnection = New-Object System.Data.SqlClient.SqlConnection($connectionString)
        try {
            $databaseConnection.Open()
            $query = $databaseConnection.CreateCommand()
            $query.CommandText = "SELECT s.name AS SchemaName, t.name AS TableName FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE t.is_ms_shipped = 0;"
            $reader = $query.ExecuteReader()
            $tables = @()
            try {
                while ($reader.Read()) {
                    $schema = $reader.GetString(0)
                    $table = $reader.GetString(1)
                    if ($schema -cne 'dbo' -or $table -cnotin $expectedTables) {
                        throw 'Conflicting tables exist in VaultFinanceDb. Nothing has been modified.'
                    }
                    $tables += $table
                }
            } finally {
                $reader.Dispose()
            }

            foreach ($table in $tables | Where-Object { $_ -cne '__EFMigrationsHistory' }) {
                # Table names come exclusively from the fixed allowlist above.
                $query.CommandText = "IF EXISTS (SELECT 1 FROM [dbo].[$table]) SELECT 1 ELSE SELECT 0;"
                if ([int]$query.ExecuteScalar() -eq 1) {
                    throw 'VaultFinanceDb contains existing data. Stop and review it before migrating; nothing has been modified.'
                }
            }

            if ($tables -ccontains '__EFMigrationsHistory') {
                $query.CommandText = 'SELECT [MigrationId] FROM [dbo].[__EFMigrationsHistory];'
                $reader = $query.ExecuteReader()
                $history = @()
                try { while ($reader.Read()) { $history += $reader.GetString(0) } }
                finally { $reader.Dispose() }
                if ($history.Count -ne 1 -or $history[0] -cne $migrationId -or $tables.Count -ne $expectedTables.Count) {
                    throw 'Existing migration history/schema does not exactly match this initial migration. Nothing has been modified.'
                }
            } elseif ($tables.Count -ne 0) {
                throw 'Existing tables have no matching migration history. Nothing has been modified.'
            }
            Write-Output 'Preflight passed: VaultFinanceDb contains no application data or foreign tables.'
        } finally {
            $databaseConnection.Dispose()
        }
    } else {
        Write-Output 'Preflight passed: VaultFinanceDb does not exist. EF will create that database and apply its schema.'
    }

    # The master connection above is read-only and disposed. EF receives only the validated target.
    $env:ConnectionStrings__DefaultConnection = $connectionString
    & dotnet ef database update $migrationId --project backend/src/VaultFinance.Infrastructure --startup-project backend/src/VaultFinance.Api --context VaultFinanceDbContext -- --environment Development
    if ($LASTEXITCODE -ne 0) { throw 'EF migration failed. Inspect the preceding error; no errors were suppressed.' }
} finally {
    [Environment]::SetEnvironmentVariable('ConnectionStrings__DefaultConnection', $previousConnection)
    Pop-Location
}
