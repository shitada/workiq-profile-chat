$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$subscription = '00000000-0000-0000-0000-000000000000'
$scope = "/subscriptions/$subscription/resourceGroups/example-group"
$captured = @{}
$storageFixture = @{
    name = 'examplestorage'
    location = 'eastus2'
    kind = 'StorageV2'
    sku = @{ name = 'Standard_LRS'; tier = 'Standard' }
    properties = @{
        allowBlobPublicAccess = $false
        allowSharedKeyAccess = $false
        publicNetworkAccess = 'Enabled'
        creationTime = '2026-01-01T00:00:00Z'
        privateEndpointConnections = @()
        encryption = @{
            services = @{ blob = @{ enabled = $true; lastEnabledTime = '2026-01-01T00:00:00Z' } }
        }
    }
}
$vaultFixture = @{
    name = 'example-vault'
    location = 'eastus2'
    properties = @{
        enableRbacAuthorization = $true
        enablePurgeProtection = $true
        vaultUri = 'https://example-vault.vault.azure.net/'
        provisioningState = 'Succeeded'
    }
}

function az {
    $arguments = @($args)
    if ($arguments[0] -ne 'rest' -or $arguments -notcontains 'GET') {
        throw 'The snapshot script attempted an Azure mutation.'
    }
    $global:LASTEXITCODE = 0
    $uri = $arguments[([Array]::IndexOf($arguments, '--uri') + 1)]
    if ($uri -like '*Microsoft.Storage/storageAccounts/*') {
        return $storageFixture | ConvertTo-Json -Depth 20
    }
    if ($uri -like '*Microsoft.KeyVault/vaults/*') {
        return $vaultFixture | ConvertTo-Json -Depth 20
    }
    if ($uri -like '*Microsoft.Web/sites/*') {
        return @{
            properties = @{
                functionAppConfig = @{
                    deployment = @{
                        storage = @{ value = 'https://examplestorage.blob.core.windows.net/deploymentpackage' }
                    }
                }
            }
        } | ConvertTo-Json -Depth 20
    }
    throw 'Unexpected Azure resource read.'
}

function azd {
    $arguments = @($args)
    $global:LASTEXITCODE = 0
    if ($arguments -contains 'get-value') { return $subscription }
    if ($arguments -contains 'set') {
        $captured[$arguments[-2]] = $arguments[-1]
        return
    }
    throw 'Unexpected azd operation.'
}

$parameters = @{
    SubscriptionId = $subscription
    ResourceGroupName = 'example-group'
    FunctionAppName = 'example-function'
    StorageAccountName = 'examplestorage'
    KeyVaultName = 'example-vault'
    FunctionSubnetResourceId = "$scope/providers/Microsoft.Network/virtualNetworks/example-network/subnets/functions"
}
$previousUserAgent = [Environment]::GetEnvironmentVariable('AZURE_DEV_USER_AGENT')
try {
    & (Join-Path $root 'scripts\set-private-cutover-state.ps1') @parameters
    $storage = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String(
        $captured.PRIVATE_STORAGE_STATE_BASE64)) | ConvertFrom-Json -AsHashtable
    $vault = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String(
        $captured.PRIVATE_VAULT_STATE_BASE64)) | ConvertFrom-Json -AsHashtable
    if ($storage.sku.name -ne 'Standard_LRS' -or $storage.tags.Count -ne 0 -or
        $storage.properties.ContainsKey('creationTime') -or
        $storage.properties.ContainsKey('privateEndpointConnections') -or
        $storage.properties.encryption.services.blob.ContainsKey('lastEnabledTime') -or
        $vault.properties.ContainsKey('vaultUri') -or
        $vault.properties.ContainsKey('provisioningState')) {
        throw 'The snapshot did not preserve writable state or remove read-only fields.'
    }

    $storageFixture.identity = @{ type = 'SystemAssigned' }
    $rejected = $false
    try {
        & (Join-Path $root 'scripts\set-private-cutover-state.ps1') @parameters
    } catch {
        if ($_.Exception.Message -notlike '*identity-preserving template*') { throw }
        $rejected = $true
    }
    if (-not $rejected) { throw 'An identity-enabled Storage account was not rejected.' }
    Write-Host 'Cutover snapshot safety tests passed.'
} finally {
    [Environment]::SetEnvironmentVariable('AZURE_DEV_USER_AGENT', $previousUserAgent)
}
