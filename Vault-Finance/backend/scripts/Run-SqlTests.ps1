[CmdletBinding()]
param(
    [switch]$CreateDatabase,
    [switch]$NoBuild,
    [string]$Filter
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$previousEnabled = $env:VAULTFINANCE_SQL_TESTS
$previousCreate = $env:VAULTFINANCE_SQL_TESTS_CREATE_DATABASE

Push-Location -LiteralPath $projectRoot
try {
    # The fixture requires SqlTestConnection from API User Secrets or environment.
    # It rejects DefaultConnection, unknown/remote targets and unmarked existing databases.
    $env:VAULTFINANCE_SQL_TESTS = '1'
    $env:VAULTFINANCE_SQL_TESTS_CREATE_DATABASE = if ($CreateDatabase) { '1' } else { '0' }
    $testArguments = @(
        'test', 'VaultFinance.slnx', '--no-restore',
        '--logger', 'trx;LogFilePrefix=sql-journal',
        '--results-directory', '.local/test-results/sql-journal'
    )
    if ($NoBuild) { $testArguments += '--no-build' }
    if ($Filter) { $testArguments += @('--filter', $Filter) }
    & dotnet @testArguments
    if ($LASTEXITCODE -ne 0) {
        throw 'SQL validation failed. Review the test output; no database cleanup was performed.'
    }
} finally {
    [Environment]::SetEnvironmentVariable('VAULTFINANCE_SQL_TESTS', $previousEnabled)
    [Environment]::SetEnvironmentVariable('VAULTFINANCE_SQL_TESTS_CREATE_DATABASE', $previousCreate)
    Pop-Location
}
