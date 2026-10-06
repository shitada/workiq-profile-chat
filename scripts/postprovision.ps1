[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$networkMigration = [Environment]::GetEnvironmentVariable('PROFILE_CHAT_NETWORK_MIGRATION')
if ($networkMigration -eq 'true') {
    Write-Host 'Network migration: skipping OAuth connection and agent updates. Existing configuration is preserved.'
    return
}
if (-not [string]::IsNullOrWhiteSpace($networkMigration) -and $networkMigration -ne 'false') {
    throw 'PROFILE_CHAT_NETWORK_MIGRATION must be true or false.'
}

$required = @(
    'AZURE_TENANT_ID',
    'SPA_APP_OBJECT_ID',
    'API_APP_OBJECT_ID',
    'API_MANAGED_IDENTITY_CLIENT_ID',
    'API_MANAGED_IDENTITY_PRINCIPAL_ID',
    'WEB_URL',
    'API_URL',
    'AZURE_RESOURCE_GROUP',
    'AZURE_APPLICATION_INSIGHTS_NAME',
    'WORKIQ_APP_OBJECT_ID',
    'WORKIQ_CLIENT_ID',
    'WORKIQ_CLIENT_SECRET',
    'AZURE_AI_PROJECT_ID',
    'AZURE_AI_PROJECT_ENDPOINT'
)
foreach ($name in $required) {
    if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($name))) {
        throw "$name is required after provisioning."
    }
}

function Invoke-AzJson {
    param([Parameter(Mandatory)][string[]]$Arguments)
    $output = & az @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Azure CLI failed: az $($Arguments -join ' ')"
    }
    if ([string]::IsNullOrWhiteSpace(($output -join ''))) {
        return $null
    }
    return ($output -join [Environment]::NewLine) | ConvertFrom-Json
}

function Invoke-GraphPatch {
    param(
        [Parameter(Mandatory)][string]$Uri,
        [Parameter(Mandatory)][hashtable]$Body
    )
    $tempFile = [IO.Path]::GetTempFileName()
    try {
        $Body | ConvertTo-Json -Depth 20 -Compress |
            Set-Content -Path $tempFile -Encoding utf8NoBOM
        & az rest --method PATCH --uri $Uri --headers 'Content-Type=application/json' --body "@$tempFile" --output none
        if ($LASTEXITCODE -ne 0) {
            throw "Microsoft Graph PATCH failed for $Uri."
        }
    } finally {
        Remove-Item -LiteralPath $tempFile -Force -ErrorAction SilentlyContinue
    }
}

$spaDocument = Invoke-AzJson @(
    'rest', '--method', 'GET',
    '--uri', "https://graph.microsoft.com/v1.0/applications/$($env:SPA_APP_OBJECT_ID)",
    '--output', 'json'
)
$spaRedirects = @($spaDocument.spa.redirectUris) +
    @('http://localhost:5173/redirect.html', "$($env:WEB_URL)/redirect.html") |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
    Where-Object {
        $_ -ne 'http://localhost:5173' -and
        $_ -ne $env:WEB_URL
    } |
    Sort-Object -Unique
Invoke-GraphPatch -Uri "https://graph.microsoft.com/v1.0/applications/$($env:SPA_APP_OBJECT_ID)" -Body @{
    spa = @{
        redirectUris = @($spaRedirects)
    }
}

$ficName = "workiq-profile-chat-$($env:AZURE_ENV_NAME)"
$ficIssuer = "https://login.microsoftonline.com/$($env:AZURE_TENANT_ID)/v2.0"
$ficAudience = 'api://AzureADTokenExchange'
$credentials = Invoke-AzJson @(
    'ad', 'app', 'federated-credential', 'list',
    '--id', $env:API_APP_OBJECT_ID,
    '--output', 'json'
)
$existingFic = @($credentials) | Where-Object name -eq $ficName | Select-Object -First 1
$ficMatches = $null -ne $existingFic -and
    $existingFic.issuer -eq $ficIssuer -and
    $existingFic.subject -eq $env:API_MANAGED_IDENTITY_PRINCIPAL_ID -and
    @($existingFic.audiences) -contains $ficAudience

if ($null -ne $existingFic -and -not $ficMatches) {
    & az ad app federated-credential delete `
        --id $env:API_APP_OBJECT_ID `
        --federated-credential-id $existingFic.id
    if ($LASTEXITCODE -ne 0) {
        throw 'Failed to remove the stale managed identity federated credential.'
    }
}

if (-not $ficMatches) {
    $ficFile = [IO.Path]::GetTempFileName()
    try {
        @{
            name = $ficName
            issuer = $ficIssuer
            subject = $env:API_MANAGED_IDENTITY_PRINCIPAL_ID
            audiences = @($ficAudience)
            description = 'Function managed identity assertion for OBO.'
        } | ConvertTo-Json -Compress |
            Set-Content -Path $ficFile -Encoding utf8NoBOM
        & az ad app federated-credential create `
            --id $env:API_APP_OBJECT_ID `
            --parameters "@$ficFile" `
            --output none
        if ($LASTEXITCODE -ne 0) {
            throw 'Failed to create the managed identity federated credential.'
        }
    } finally {
        Remove-Item -LiteralPath $ficFile -Force -ErrorAction SilentlyContinue
    }
}

$workIqConnectionName = 'workiq-mcp'
$workIqConnected = $false
for ($attempt = 1; $attempt -le 6 -and -not $workIqConnected; $attempt++) {
    & azd ai connection create $workIqConnectionName `
        --project-endpoint $env:AZURE_AI_PROJECT_ENDPOINT `
        --kind remote-tool `
        --target 'https://workiq.svc.cloud.microsoft/mcp' `
        --auth-type oauth2 `
        --client-id $env:WORKIQ_CLIENT_ID `
        --client-secret $env:WORKIQ_CLIENT_SECRET `
        --authorization-url "https://login.microsoftonline.com/$($env:AZURE_TENANT_ID)/oauth2/v2.0/authorize" `
        --token-url "https://login.microsoftonline.com/$($env:AZURE_TENANT_ID)/oauth2/v2.0/token" `
        --refresh-url "https://login.microsoftonline.com/$($env:AZURE_TENANT_ID)/oauth2/v2.0/token" `
        --scopes 'api://workiq.svc.cloud.microsoft/WorkIQAgent.Ask,offline_access' `
        --force `
        --no-prompt `
        --output json | Out-Null
    $workIqConnected = $LASTEXITCODE -eq 0
    if (-not $workIqConnected -and $attempt -lt 6) {
        Start-Sleep -Seconds ([Math]::Min(60, 5 * [Math]::Pow(2, $attempt - 1)))
    }
}
if (-not $workIqConnected) {
    throw 'Failed to create or update the Foundry Work IQ OAuth connection.'
}

$workIqConnectionId = "$($env:AZURE_AI_PROJECT_ID)/connections/$workIqConnectionName"
[Environment]::SetEnvironmentVariable('WORKIQ_CONNECTION_ID', $workIqConnectionId)
& azd env set WORKIQ_CONNECTION_ID $workIqConnectionId | Out-Null

$connection = Invoke-AzJson @(
    'rest', '--method', 'GET',
    '--uri', "https://management.azure.com${workIqConnectionId}?api-version=2025-12-01",
    '--output', 'json'
)
$redirectUri = $connection.properties.redirectUrl

if (-not [string]::IsNullOrWhiteSpace($redirectUri)) {
    $workIqDocument = Invoke-AzJson @(
        'rest', '--method', 'GET',
        '--uri', "https://graph.microsoft.com/v1.0/applications/$($env:WORKIQ_APP_OBJECT_ID)",
        '--output', 'json'
    )
    $webRedirects = @($workIqDocument.web.redirectUris) + @($redirectUri) |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Sort-Object -Unique
    Invoke-GraphPatch -Uri "https://graph.microsoft.com/v1.0/applications/$($env:WORKIQ_APP_OBJECT_ID)" -Body @{
        web = @{
            redirectUris = @($webRedirects)
        }
    }
} else {
    throw 'The Work IQ connection did not expose properties.redirectUrl.'
}

$appInsights = Invoke-AzJson @(
    'monitor', 'app-insights', 'component', 'show',
    '--resource-group', $env:AZURE_RESOURCE_GROUP,
    '--app', $env:AZURE_APPLICATION_INSIGHTS_NAME,
    '--query', '{id:id,connectionString:connectionString}',
    '--output', 'json'
)
$appInsightsConnected = $false
for ($attempt = 1; $attempt -le 6 -and -not $appInsightsConnected; $attempt++) {
    & azd ai connection create application-insights `
        --project-endpoint $env:AZURE_AI_PROJECT_ENDPOINT `
        --kind app-insights `
        --target $appInsights.id `
        --auth-type api-key `
        --key $appInsights.connectionString `
        --force `
        --no-prompt `
        --output json | Out-Null
    $appInsightsConnected = $LASTEXITCODE -eq 0
    if (-not $appInsightsConnected -and $attempt -lt 6) {
        Start-Sleep -Seconds ([Math]::Min(60, 5 * [Math]::Pow(2, $attempt - 1)))
    }
}
if (-not $appInsightsConnected) {
    throw 'Failed to connect Application Insights to the Foundry project.'
}

foreach ($versionSetting in @('REPORT_AGENT_VERSION', 'REPORT_COLLECTOR_VERSION')) {
    if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($versionSetting))) {
        throw "$versionSetting is required. Create the shared reporting agent definitions before provisioning."
    }
}
Write-Host 'Reporting agents use pinned common definitions. Legacy profile-agent creation is skipped.'

& azd env set VITE_TENANT_ID $env:AZURE_TENANT_ID | Out-Null
& azd env set VITE_SPA_CLIENT_ID $env:SPA_CLIENT_ID | Out-Null
& azd env set VITE_API_CLIENT_ID $env:API_CLIENT_ID | Out-Null
& azd env set VITE_API_URL $env:API_URL | Out-Null
& azd env set VITE_REPORT_PROVIDER workiq | Out-Null

Write-Host 'Post-provision identity, secret, redirect, and Foundry agent configuration completed.'
