[CmdletBinding(SupportsShouldProcess)]
param(
    [int]$ValidityDays = 365
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

foreach ($name in @(
    'WORKIQ_APP_OBJECT_ID',
    'WORKIQ_CLIENT_ID',
    'AZURE_KEY_VAULT_NAME',
    'AZURE_SUBSCRIPTION_ID',
    'AZURE_RESOURCE_GROUP',
    'AZURE_TENANT_ID',
    'AZURE_AI_PROJECT_ENDPOINT'
)) {
    if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($name))) {
        throw "$name is required. Run this script from an initialized azd environment."
    }
}

if (-not $PSCmdlet.ShouldProcess($env:WORKIQ_APP_OBJECT_ID, 'Rotate Work IQ OAuth client secret')) {
    return
}

$endDate = [DateTimeOffset]::UtcNow.AddDays($ValidityDays)
$credentialDisplayName = "rotation-$([DateTime]::UtcNow.ToString('yyyyMMddHHmmss'))"
$credential = az ad app credential reset `
    --id $env:WORKIQ_APP_OBJECT_ID `
    --append `
    --display-name $credentialDisplayName `
    --end-date $endDate.UtcDateTime.ToString('yyyy-MM-ddTHH:mm:ssZ') `
    --query '{password:password}' `
    --output json | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) {
    throw 'Failed to create a replacement Work IQ OAuth secret.'
}
$application = az rest `
    --method GET `
    --uri "https://graph.microsoft.com/v1.0/applications/$($env:WORKIQ_APP_OBJECT_ID)" `
    --output json | ConvertFrom-Json
$credentialMetadata = @($application.passwordCredentials) |
    Where-Object displayName -eq $credentialDisplayName |
    Select-Object -First 1
if ($null -eq $credentialMetadata) {
    throw 'The replacement credential metadata could not be resolved.'
}

try {
    $secretFile = [IO.Path]::GetTempFileName()
    try {
        @{
            properties = @{
                value = $credential.password
                contentType = 'Work IQ OAuth client secret'
                attributes = @{
                    enabled = $true
                    exp = $endDate.ToUnixTimeSeconds()
                }
            }
        } | ConvertTo-Json -Depth 10 -Compress |
            Set-Content -Path $secretFile -Encoding utf8NoBOM
        $secretUri = "https://management.azure.com/subscriptions/$($env:AZURE_SUBSCRIPTION_ID)/resourceGroups/$($env:AZURE_RESOURCE_GROUP)/providers/Microsoft.KeyVault/vaults/$($env:AZURE_KEY_VAULT_NAME)/secrets/workiq-oauth-client-secret?api-version=2025-05-01"
        az rest `
            --method PUT `
            --uri $secretUri `
            --headers 'Content-Type=application/json' `
            --body "@$secretFile" `
            --output none
        if ($LASTEXITCODE -ne 0) {
            throw 'Failed to update the Key Vault secret through ARM.'
        }
    } finally {
        Remove-Item -LiteralPath $secretFile -Force -ErrorAction SilentlyContinue
    }

    azd ai connection create workiq-mcp `
        --project-endpoint $env:AZURE_AI_PROJECT_ENDPOINT `
        --kind remote-tool `
        --target 'https://workiq.svc.cloud.microsoft/mcp' `
        --auth-type oauth2 `
        --client-id $env:WORKIQ_CLIENT_ID `
        --client-secret $credential.password `
        --authorization-url "https://login.microsoftonline.com/$($env:AZURE_TENANT_ID)/oauth2/v2.0/authorize" `
        --token-url "https://login.microsoftonline.com/$($env:AZURE_TENANT_ID)/oauth2/v2.0/token" `
        --refresh-url "https://login.microsoftonline.com/$($env:AZURE_TENANT_ID)/oauth2/v2.0/token" `
        --scopes 'api://workiq.svc.cloud.microsoft/WorkIQAgent.Ask,offline_access' `
        --force `
        --no-prompt `
        --output json | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw 'Failed to update the Foundry Work IQ connection.'
    }

    azd env set WORKIQ_CLIENT_SECRET $credential.password | Out-Null
    azd env set WORKIQ_CLIENT_SECRET_KEY_ID $credentialMetadata.keyId | Out-Null
    azd env set WORKIQ_CLIENT_SECRET_EXPIRES_ON $endDate.ToString('o') | Out-Null
    Write-Host 'The replacement secret is active. Remove the previous credential after end-user consent validation.'
} catch {
    Write-Error 'Rotation did not complete. The previous secret was not removed.'
    throw
}
