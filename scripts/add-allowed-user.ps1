[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$UserPrincipalName,

    [ValidateSet('Foundry Agent Consumer', 'Foundry User')]
    [string]$FoundryRole = 'Foundry Agent Consumer'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$groupId = [Environment]::GetEnvironmentVariable('ALLOWED_GROUP_ID')
if ([string]::IsNullOrWhiteSpace($groupId)) {
    $groupId = azd env get-value ALLOWED_GROUP_ID
}
if ([string]::IsNullOrWhiteSpace($groupId)) {
    throw 'ALLOWED_GROUP_ID is not available in the selected azd environment.'
}

$user = az ad user show `
    --id $UserPrincipalName `
    --query '{id:id,userPrincipalName:userPrincipalName}' `
    --output json | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or $null -eq $user) {
    throw "User '$UserPrincipalName' was not found."
}

$isMember = az ad group member check `
    --group $groupId `
    --member-id $user.id `
    --query value `
    --output tsv
if ($LASTEXITCODE -ne 0) {
    throw 'Failed to check the current allowlist membership.'
}

if ($isMember -ne 'true') {
    az ad group member add --group $groupId --member-id $user.id
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to add '$($user.userPrincipalName)' to the allowlist group."
    }
}

$verified = az ad group member check `
    --group $groupId `
    --member-id $user.id `
    --query value `
    --output tsv
if ($verified -ne 'true') {
    throw 'Allowlist membership verification failed.'
}

$groupName = az ad group show --group $groupId --query displayName --output tsv
$projectId = [Environment]::GetEnvironmentVariable('AZURE_AI_PROJECT_ID')
if ([string]::IsNullOrWhiteSpace($projectId)) {
    $projectId = azd env get-value AZURE_AI_PROJECT_ID
}
if ([string]::IsNullOrWhiteSpace($projectId)) {
    throw 'AZURE_AI_PROJECT_ID is not available in the selected azd environment.'
}

$foundryRoleId = az role definition list `
    --name $FoundryRole `
    --query '[0].id' `
    --output tsv
if ([string]::IsNullOrWhiteSpace($foundryRoleId)) {
    throw "The $FoundryRole role definition was not found."
}

$foundryAssignment = az role assignment list `
    --assignee-object-id $user.id `
    --scope $projectId `
    --query "[?roleDefinitionId=='$foundryRoleId'] | [0].id" `
    --output tsv
if ([string]::IsNullOrWhiteSpace($foundryAssignment)) {
    az role assignment create `
        --assignee-object-id $user.id `
        --assignee-principal-type User `
        --role $foundryRoleId `
        --scope $projectId `
        --output none
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to grant $FoundryRole to '$($user.userPrincipalName)'."
    }
}

Write-Host "Allowed user: $($user.userPrincipalName)"
Write-Host "Group: $groupName"
Write-Host 'Direct membership: true'
Write-Host "Foundry role: $FoundryRole"
