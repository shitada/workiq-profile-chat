[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SubscriptionId,
    [Parameter(Mandatory)][string]$ResourceGroupName,
    [Parameter(Mandatory)][string]$FunctionAppName,
    [Parameter(Mandatory)][string]$StorageAccountName,
    [Parameter(Mandatory)][string]$KeyVaultName,
    [Parameter(Mandatory)][string]$FunctionSubnetResourceId
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$env:AZURE_DEV_USER_AGENT = 'microsoft_foundry_skill'
$cutoverDirectory = Join-Path (Split-Path $PSScriptRoot -Parent) 'network\cutover'

function Read-AzureResource {
    param([string]$Id, [string]$ApiVersion)
    $result = & az rest --method GET --uri "https://management.azure.com${Id}?api-version=$ApiVersion" --output json
    if ($LASTEXITCODE -ne 0) { throw "Cannot read resource type at $Id." }
    return ($result -join [Environment]::NewLine) | ConvertFrom-Json -AsHashtable
}

function Set-CutoverValue {
    param([string]$Name, [string]$Value)
    & azd -C $cutoverDirectory env set $Name $Value
    if ($LASTEXITCODE -ne 0) { throw "Cannot set cutover environment value $Name." }
}

$environmentSubscription = & azd -C $cutoverDirectory env get-value AZURE_SUBSCRIPTION_ID
if ($LASTEXITCODE -ne 0 -or $environmentSubscription.Trim() -ne $SubscriptionId) {
    throw 'Initialize the cutover azd environment with the intended subscription first.'
}
$scope = "/subscriptions/$SubscriptionId/resourceGroups/$ResourceGroupName"
if (-not $FunctionSubnetResourceId.StartsWith("$scope/providers/Microsoft.Network/virtualNetworks/", [StringComparison]::OrdinalIgnoreCase) -or
    $FunctionSubnetResourceId -notmatch '/subnets/[^/]+$') {
    throw 'The integration subnet must be inside the application resource group.'
}
$app = Read-AzureResource "$scope/providers/Microsoft.Web/sites/$FunctionAppName" '2024-11-01'
$storage = Read-AzureResource "$scope/providers/Microsoft.Storage/storageAccounts/$StorageAccountName" '2023-05-01'
$vault = Read-AzureResource "$scope/providers/Microsoft.KeyVault/vaults/$KeyVaultName" '2024-11-01'
$storageHost = ([uri]$app.properties.functionAppConfig.deployment.storage.value).DnsSafeHost
if ($storageHost.Split('.')[0] -ne $StorageAccountName) {
    throw 'Storage does not match the Function deployment storage.'
}
if ($storage['identity']) {
    throw 'This migration requires an identity-preserving template for identity-enabled Storage; refusing to remove it.'
}
if ($storage.properties.allowSharedKeyAccess -ne $false -or
    $storage.properties.allowBlobPublicAccess -ne $false -or
    $vault.properties.enableRbacAuthorization -ne $true -or
    $vault.properties.enablePurgeProtection -ne $true) {
    throw 'The existing authentication and protection settings differ from the approved baseline.'
}

$storageProperties = @{}
$readOnlyStorage = @(
    'creationTime', 'keyCreationTime', 'primaryEndpoints', 'secondaryEndpoints',
    'primaryLocation', 'secondaryLocation', 'privateEndpointConnections',
    'provisioningState', 'statusOfPrimary', 'statusOfSecondary',
    'lastGeoFailoverTime', 'geoReplicationStats'
)
foreach ($key in $storage.properties.Keys) {
    if ($key -notin $readOnlyStorage) { $storageProperties[$key] = $storage.properties[$key] }
}
foreach ($service in $storageProperties.encryption.services.Values) {
    $null = $service.Remove('lastEnabledTime')
}
$vaultProperties = @{}
foreach ($key in $vault.properties.Keys) {
    if ($key -notin @('provisioningState', 'vaultUri', 'privateEndpointConnections')) {
        $vaultProperties[$key] = $vault.properties[$key]
    }
}
$storageState = @{
    name = $storage.name
    location = $storage.location
    kind = $storage.kind
    sku = @{ name = $storage.sku.name }
    tags = $(if ($storage['tags']) { $storage['tags'] } else { @{} })
    properties = $storageProperties
}
$vaultState = @{
    name = $vault.name
    location = $vault.location
    tags = $(if ($vault['tags']) { $vault['tags'] } else { @{} })
    properties = $vaultProperties
}
Set-CutoverValue 'AZURE_RESOURCE_GROUP' $ResourceGroupName
Set-CutoverValue 'AZURE_FUNCTION_NAME' $FunctionAppName
Set-CutoverValue 'AZURE_FUNCTION_SUBNET_ID' $FunctionSubnetResourceId
Set-CutoverValue 'PRIVATE_STORAGE_STATE_BASE64' ([Convert]::ToBase64String(
    [Text.Encoding]::UTF8.GetBytes(($storageState | ConvertTo-Json -Depth 30 -Compress))))
Set-CutoverValue 'PRIVATE_VAULT_STATE_BASE64' ([Convert]::ToBase64String(
    [Text.Encoding]::UTF8.GetBytes(($vaultState | ConvertTo-Json -Depth 30 -Compress))))
Write-Host 'Writable resource state captured in ignored azd environment. No Azure resources were changed.'
