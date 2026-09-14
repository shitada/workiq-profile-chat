targetScope = 'subscription'

@minLength(1)
@maxLength(64)
param environmentName string

@description('Single Azure region for every deployable regional resource.')
param location string

@description('Existing or new resource group name.')
param resourceGroupName string

param tenantId string
param deployerPrincipalId string
param spaClientId string
param apiClientId string
param allowedGroupId string
@secure()
param workIqClientSecret string
param oauthCredentialExpiresOn string

var tags = {
  'azd-env-name': environmentName
  application: 'workiq-profile-chat'
  dataClassification: 'confidential'
}

resource resourceGroup 'Microsoft.Resources/resourceGroups@2024-11-01' = {
  name: resourceGroupName
  location: location
  tags: tags
}

module platform './modules/platform.bicep' = {
  name: 'workiq-profile-chat-platform'
  scope: resourceGroup
  params: {
    name: environmentName
    location: location
    tags: tags
    tenantId: tenantId
    deployerPrincipalId: deployerPrincipalId
    spaClientId: spaClientId
    apiClientId: apiClientId
    allowedGroupId: allowedGroupId
    workIqClientSecret: workIqClientSecret
    oauthCredentialExpiresOn: oauthCredentialExpiresOn
  }
}

output AZURE_LOCATION string = location
output AZURE_RESOURCE_GROUP string = resourceGroup.name
output AZURE_TENANT_ID string = tenantId
output SERVICE_API_NAME string = platform.outputs.functionAppName
output SERVICE_WEB_NAME string = platform.outputs.staticWebAppName
output API_URL string = platform.outputs.apiUrl
output WEB_URL string = platform.outputs.webUrl
output AZURE_FUNCTION_NAME string = platform.outputs.functionAppName
output AZURE_STATIC_WEB_APP_NAME string = platform.outputs.staticWebAppName
output AZURE_KEY_VAULT_NAME string = platform.outputs.keyVaultName
output AZURE_APPLICATION_INSIGHTS_NAME string = platform.outputs.applicationInsightsName
output AZURE_LOG_ANALYTICS_WORKSPACE_ID string = platform.outputs.logAnalyticsWorkspaceId
output AZURE_AI_ACCOUNT_NAME string = platform.outputs.foundryAccountName
output AZURE_AI_PROJECT_NAME string = platform.outputs.foundryProjectName
output AZURE_AI_PROJECT_ENDPOINT string = platform.outputs.foundryProjectEndpoint
output AZURE_AI_PROJECT_ID string = platform.outputs.foundryProjectId
output API_MANAGED_IDENTITY_CLIENT_ID string = platform.outputs.functionIdentityClientId
output API_MANAGED_IDENTITY_PRINCIPAL_ID string = platform.outputs.functionIdentityPrincipalId
