$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$saved = @{}
foreach ($name in @('PROFILE_CHAT_NETWORK_MIGRATION', 'AZURE_ENV_NAME', 'AZURE_TENANT_ID')) {
    $saved[$name] = [Environment]::GetEnvironmentVariable($name)
}

function az { throw 'Unexpected Azure CLI call during hook safety test.' }
function azd { throw 'Unexpected azd call during hook safety test.' }

try {
    $env:AZURE_ENV_NAME = ''
    $env:AZURE_TENANT_ID = ''
    foreach ($file in @('preprovision.ps1', 'postprovision.ps1')) {
        $path = Join-Path $root "scripts\$file"
        $tokens = $null
        $parseErrors = $null
        $null = [System.Management.Automation.Language.Parser]::ParseFile(
            $path, [ref]$tokens, [ref]$parseErrors)
        if ($parseErrors.Count -gt 0) {
            throw "$file has PowerShell syntax errors."
        }

        $env:PROFILE_CHAT_NETWORK_MIGRATION = 'true'
        & $path

        $env:PROFILE_CHAT_NETWORK_MIGRATION = 'invalid'
        $rejected = $false
        try {
            & $path
        } catch {
            if ($_.Exception.Message -notlike '*must be true or false*') { throw }
            $rejected = $true
        }
        if (-not $rejected) { throw "$file accepted an invalid migration flag." }

        foreach ($normalMode in @('', 'false')) {
            $env:PROFILE_CHAT_NETWORK_MIGRATION = $normalMode
            $rejected = $false
            try {
                & $path
            } catch {
                if ($_.Exception.Message -notmatch 'AZURE_(ENV_NAME|TENANT_ID).*required|AZURE_ENV_NAME must be set') {
                    throw
                }
                $rejected = $true
            }
            if (-not $rejected) { throw "$file skipped normal prerequisite validation." }
        }
    }
    Write-Host 'Network migration hook safety tests passed.'
} finally {
    foreach ($name in $saved.Keys) {
        [Environment]::SetEnvironmentVariable($name, $saved[$name])
    }
}
