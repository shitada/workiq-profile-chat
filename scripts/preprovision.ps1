[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$required = @(
    'AZURE_ENV_NAME',
    'AZURE_SUBSCRIPTION_ID',
    'AZURE_TENANT_ID',
    'AZURE_LOCATION',
    'AZURE_RESOURCE_GROUP'
)

foreach ($name in $required) {
    if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($name))) {
        throw "$name must be set in the azd environment."
    }
}

$subscriptionId = $env:AZURE_SUBSCRIPTION_ID
$tenantId = $env:AZURE_TENANT_ID
$environmentName = $env:AZURE_ENV_NAME
$suffix = ($environmentName.ToLowerInvariant() -replace '[^a-z0-9-]', '-').Trim('-')
$groupName = "WorkIQ Profile Chat Users - $suffix"
$spaName = "workiq-profile-chat-spa-$suffix"
$apiName = "workiq-profile-chat-api-$suffix"
$workIqName = "workiq-profile-chat-workiq-$suffix"
$workIqServiceAppId = 'fdcc1f02-fc51-4226-8753-f668596af7f7'

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

function Set-AzdValue {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Value
    )

    & azd env set $Name $Value | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to set azd environment value $Name."
    }
    [Environment]::SetEnvironmentVariable($Name, $Value)
}

function Get-OrCreateApplication {
    param([Parameter(Mandatory)][string]$DisplayName)

    $apps = @(Invoke-AzJson @(
        'ad', 'app', 'list',
        '--display-name', $DisplayName,
        '--query', "[?displayName=='$DisplayName'].{appId:appId,id:id,displayName:displayName}",
        '--output', 'json'
    ))

    if ($apps.Count -gt 1) {
        throw "Multiple app registrations named '$DisplayName' exist."
    }
    if ($apps.Count -eq 1) {
        return $apps[0]
    }

    return Invoke-AzJson @(
        'ad', 'app', 'create',
        '--display-name', $DisplayName,
        '--sign-in-audience', 'AzureADMyOrg',
        '--query', '{appId:appId,id:id,displayName:displayName}',
        '--output', 'json'
    )
}

function Ensure-ServicePrincipal {
    param([Parameter(Mandatory)][string]$AppId)

    $servicePrincipal = Invoke-AzJson @(
        'ad', 'sp', 'list',
        '--filter', "appId eq '$AppId'",
        '--query', '[0].{id:id,appId:appId}',
        '--output', 'json'
    )
    if ($null -ne $servicePrincipal) {
        return $servicePrincipal
    }

    return Invoke-AzJson @(
        'ad', 'sp', 'create',
        '--id', $AppId,
        '--query', '{id:id,appId:appId}',
        '--output', 'json'
    )
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

az account set --subscription $subscriptionId
$account = Invoke-AzJson @(
    'account', 'show',
    '--query', '{subscription:id,tenant:tenantId,user:user.name}',
    '--output', 'json'
)
if ($account.tenant -ne $tenantId) {
    throw "Azure CLI is signed in to tenant '$($account.tenant)', not '$tenantId'."
}

$principal = Invoke-AzJson @(
    'ad', 'signed-in-user', 'show',
    '--query', '{id:id,userPrincipalName:userPrincipalName}',
    '--output', 'json'
)
Set-AzdValue -Name 'AZURE_PRINCIPAL_ID' -Value $principal.id

$providers = @(
    'Microsoft.Web',
    'Microsoft.CognitiveServices',
    'Microsoft.Storage',
    'Microsoft.KeyVault',
    'Microsoft.Insights',
    'Microsoft.OperationalInsights'
    'Microsoft.ManagedIdentity'
)
foreach ($provider in $providers) {
    & az provider register --subscription $subscriptionId --namespace $provider --wait --output none
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to register resource provider $provider."
    }
}

$groups = @(Invoke-AzJson @(
    'ad', 'group', 'list',
    '--display-name', $groupName,
    '--query', "[?displayName=='$groupName'].{id:id,displayName:displayName}",
    '--output', 'json'
))
if ($groups.Count -gt 1) {
    throw "Multiple Entra groups named '$groupName' exist."
}
$group = if ($groups.Count -eq 1) {
    $groups[0]
} else {
    Invoke-AzJson @(
        'ad', 'group', 'create',
        '--display-name', $groupName,
        '--mail-nickname', ("workiq-profile-chat-$($suffix -replace '-', '')"),
        '--query', '{id:id,displayName:displayName}',
        '--output', 'json'
    )
}

$isMember = & az ad group member check --group $group.id --member-id $principal.id --query value -o tsv
if ($isMember -ne 'true') {
    & az ad group member add --group $group.id --member-id $principal.id
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to add the deployment user to the allowlist group."
    }
}
Set-AzdValue -Name 'ALLOWED_GROUP_ID' -Value $group.id

$apiApp = Get-OrCreateApplication -DisplayName $apiName
$apiServicePrincipal = Ensure-ServicePrincipal -AppId $apiApp.appId
$apiDocument = Invoke-AzJson @(
    'rest', '--method', 'GET',
    '--uri', "https://graph.microsoft.com/v1.0/applications/$($apiApp.id)",
    '--output', 'json'
)
$scope = @($apiDocument.api.oauth2PermissionScopes) |
    Where-Object value -eq 'access_as_user' |
    Select-Object -First 1
if ($null -eq $scope) {
    $scope = [pscustomobject]@{
        adminConsentDescription = 'Access Work IQ Profile Chat as the signed-in user.'
        adminConsentDisplayName = 'Access Work IQ Profile Chat'
        id = [guid]::NewGuid().ToString()
        isEnabled = $true
        type = 'User'
        userConsentDescription = 'Access Work IQ Profile Chat as you.'
        userConsentDisplayName = 'Access Work IQ Profile Chat'
        value = 'access_as_user'
    }
}
$profileRole = @($apiDocument.appRoles) |
    Where-Object value -eq 'ProfileChat.User' |
    Select-Object -First 1
if ($null -eq $profileRole) {
    $profileRole = [pscustomobject]@{
        allowedMemberTypes = @('User')
        description = 'Use Work IQ Profile Chat.'
        displayName = 'Work IQ Profile Chat User'
        id = [guid]::NewGuid().ToString()
        isEnabled = $true
        origin = 'Application'
        value = 'ProfileChat.User'
    }
}

Invoke-GraphPatch -Uri "https://graph.microsoft.com/v1.0/applications/$($apiApp.id)" -Body @{
    identifierUris = @("api://$($apiApp.appId)")
    groupMembershipClaims = 'SecurityGroup'
    appRoles = @($apiDocument.appRoles |
        Where-Object value -ne 'ProfileChat.User') + @($profileRole)
    api = @{
        oauth2PermissionScopes = @($apiDocument.api.oauth2PermissionScopes |
            Where-Object value -ne 'access_as_user') + @($scope)
    }
}
Invoke-GraphPatch -Uri "https://graph.microsoft.com/v1.0/servicePrincipals/$($apiServicePrincipal.id)" -Body @{
    appRoleAssignmentRequired = $true
}

$foundryServicePrincipal = Invoke-AzJson @(
    'rest', '--method', 'GET',
    '--uri', "https://graph.microsoft.com/v1.0/servicePrincipals?`$filter=servicePrincipalNames/any(x:x eq 'https://ai.azure.com')",
    '--query', 'value[0].{id:id,appId:appId,oauth2PermissionScopes:oauth2PermissionScopes}',
    '--output', 'json'
)
if ($null -eq $foundryServicePrincipal) {
    throw 'The https://ai.azure.com service principal was not found in this tenant.'
}
$foundryDelegatedScope = @($foundryServicePrincipal.oauth2PermissionScopes) |
    Where-Object value -eq 'user_impersonation' |
    Select-Object -First 1
if ($null -eq $foundryDelegatedScope) {
    throw 'The Foundry user_impersonation delegated scope was not found.'
}
$hasFoundryPermission = @($apiDocument.requiredResourceAccess) |
    Where-Object resourceAppId -eq $foundryServicePrincipal.appId |
    ForEach-Object resourceAccess |
    Where-Object id -eq $foundryDelegatedScope.id
if (-not $hasFoundryPermission) {
    & az ad app permission add `
        --id $apiApp.appId `
        --api $foundryServicePrincipal.appId `
        --api-permissions "$($foundryDelegatedScope.id)=Scope" `
        --output none
    if ($LASTEXITCODE -ne 0) {
        throw 'Failed to add the Foundry user_impersonation permission to the Function API app.'
    }
}
& az ad app permission admin-consent --id $apiApp.appId
if ($LASTEXITCODE -ne 0) {
    throw 'Global Administrator consent for the Function API Foundry permission is required.'
}

Set-AzdValue -Name 'API_CLIENT_ID' -Value $apiApp.appId
Set-AzdValue -Name 'API_APP_OBJECT_ID' -Value $apiApp.id

$spaApp = Get-OrCreateApplication -DisplayName $spaName
$null = Ensure-ServicePrincipal -AppId $spaApp.appId
$spaDocument = Invoke-AzJson @(
    'rest', '--method', 'GET',
    '--uri', "https://graph.microsoft.com/v1.0/applications/$($spaApp.id)",
    '--output', 'json'
)
$redirectUris = @($spaDocument.spa.redirectUris) + @('http://localhost:5173/redirect.html') |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
    Where-Object { $_ -ne 'http://localhost:5173' } |
    Sort-Object -Unique
Invoke-GraphPatch -Uri "https://graph.microsoft.com/v1.0/applications/$($spaApp.id)" -Body @{
    spa = @{
        redirectUris = @($redirectUris)
    }
}

$permissionExists = @($spaDocument.requiredResourceAccess) |
    Where-Object resourceAppId -eq $apiApp.appId |
    ForEach-Object resourceAccess |
    Where-Object id -eq $scope.id
if (-not $permissionExists) {
    & az ad app permission add `
        --id $spaApp.appId `
        --api $apiApp.appId `
        --api-permissions "$($scope.id)=Scope" `
        --output none
    if ($LASTEXITCODE -ne 0) {
        throw 'Failed to add the Function API delegated permission to the SPA.'
    }
}
Set-AzdValue -Name 'SPA_CLIENT_ID' -Value $spaApp.appId
Set-AzdValue -Name 'SPA_APP_OBJECT_ID' -Value $spaApp.id

$assignmentBody = @{
    principalId = $group.id
    resourceId = $apiServicePrincipal.id
    appRoleId = $profileRole.id
} | ConvertTo-Json -Compress
$existingAssignment = Invoke-AzJson @(
    'rest', '--method', 'GET',
    '--uri', "https://graph.microsoft.com/v1.0/groups/$($group.id)/appRoleAssignments",
    '--query', "value[?resourceId=='$($apiServicePrincipal.id)' && appRoleId=='$($profileRole.id)'] | [0]",
    '--output', 'json'
)
if ($null -eq $existingAssignment) {
    $assignmentFile = [IO.Path]::GetTempFileName()
    try {
        Set-Content -Path $assignmentFile -Value $assignmentBody -Encoding utf8NoBOM
        & az rest `
            --method POST `
            --uri "https://graph.microsoft.com/v1.0/servicePrincipals/$($apiServicePrincipal.id)/appRoleAssignedTo" `
            --headers 'Content-Type=application/json' `
            --body "@$assignmentFile" `
            --output none
        if ($LASTEXITCODE -ne 0) {
            throw 'Failed to assign the allowlist group to the API enterprise application.'
        }
    } finally {
        Remove-Item -LiteralPath $assignmentFile -Force -ErrorAction SilentlyContinue
    }
}

$workIqServicePrincipal = Ensure-ServicePrincipal -AppId $workIqServiceAppId
$workIqScopeId = Invoke-AzJson @(
    'ad', 'sp', 'show',
    '--id', $workIqServicePrincipal.id,
    '--query', "oauth2PermissionScopes[?value=='WorkIQAgent.Ask'].id | [0]",
    '--output', 'json'
)
if ([string]::IsNullOrWhiteSpace($workIqScopeId)) {
    throw 'The WorkIQAgent.Ask delegated permission was not found in this tenant.'
}

$workIqApp = Get-OrCreateApplication -DisplayName $workIqName
$null = Ensure-ServicePrincipal -AppId $workIqApp.appId
$workIqDocument = Invoke-AzJson @(
    'rest', '--method', 'GET',
    '--uri', "https://graph.microsoft.com/v1.0/applications/$($workIqApp.id)",
    '--output', 'json'
)
$hasWorkIqPermission = @($workIqDocument.requiredResourceAccess) |
    Where-Object resourceAppId -eq $workIqServiceAppId |
    ForEach-Object resourceAccess |
    Where-Object id -eq $workIqScopeId
if (-not $hasWorkIqPermission) {
    & az ad app permission add `
        --id $workIqApp.appId `
        --api $workIqServiceAppId `
        --api-permissions "$workIqScopeId=Scope" `
        --output none
    if ($LASTEXITCODE -ne 0) {
        throw 'Failed to add WorkIQAgent.Ask to the Work IQ OAuth app.'
    }
}

$workIqSecret = [Environment]::GetEnvironmentVariable('WORKIQ_CLIENT_SECRET')
$workIqSecretKeyId = [Environment]::GetEnvironmentVariable('WORKIQ_CLIENT_SECRET_KEY_ID')
$credentialIsCurrent = $false
if (-not [string]::IsNullOrWhiteSpace($workIqSecret) -and
    -not [string]::IsNullOrWhiteSpace($workIqSecretKeyId)) {
    $passwordCredential = @($workIqDocument.passwordCredentials) |
        Where-Object keyId -eq $workIqSecretKeyId |
        Select-Object -First 1
    $credentialIsCurrent = $null -ne $passwordCredential -and
        [DateTimeOffset]::Parse($passwordCredential.endDateTime) -gt [DateTimeOffset]::UtcNow.AddDays(30)
    if ($credentialIsCurrent) {
        Set-AzdValue -Name 'WORKIQ_CLIENT_SECRET_EXPIRES_ON' -Value (
            [DateTimeOffset]::Parse($passwordCredential.endDateTime).ToString('o')
        )
    }
}

if (-not $credentialIsCurrent) {
    $credentialDisplayName = "azd-$environmentName-$([DateTime]::UtcNow.ToString('yyyyMMddHHmmss'))"
    $credential = Invoke-AzJson @(
        'ad', 'app', 'credential', 'reset',
        '--id', $workIqApp.id,
        '--append',
        '--display-name', $credentialDisplayName,
        '--years', '1',
        '--query', '{password:password}',
        '--output', 'json'
    )
    $workIqSecret = $credential.password
    $updatedWorkIqDocument = Invoke-AzJson @(
        'rest', '--method', 'GET',
        '--uri', "https://graph.microsoft.com/v1.0/applications/$($workIqApp.id)",
        '--output', 'json'
    )
    $createdPasswordCredential = @($updatedWorkIqDocument.passwordCredentials) |
        Where-Object displayName -eq $credentialDisplayName |
        Sort-Object startDateTime -Descending |
        Select-Object -First 1
    if ($null -eq $createdPasswordCredential) {
        throw 'The new Work IQ app credential metadata could not be resolved.'
    }
    Set-AzdValue -Name 'WORKIQ_CLIENT_SECRET_KEY_ID' -Value $createdPasswordCredential.keyId
    Set-AzdValue -Name 'WORKIQ_CLIENT_SECRET_EXPIRES_ON' -Value (
        [DateTimeOffset]::Parse($createdPasswordCredential.endDateTime).ToString('o')
    )
}

Set-AzdValue -Name 'WORKIQ_CLIENT_ID' -Value $workIqApp.appId
Set-AzdValue -Name 'WORKIQ_APP_OBJECT_ID' -Value $workIqApp.id
Set-AzdValue -Name 'WORKIQ_CLIENT_SECRET' -Value $workIqSecret

& az ad app permission admin-consent --id $workIqApp.appId
if ($LASTEXITCODE -ne 0) {
    throw 'Global Administrator consent for WorkIQAgent.Ask is required before provisioning can continue.'
}

Write-Host "Entra bootstrap completed for $($principal.userPrincipalName)."
