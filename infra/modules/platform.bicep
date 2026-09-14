targetScope = 'resourceGroup'

param name string
param location string = resourceGroup().location
param tags object = {}
param tenantId string
param deployerPrincipalId string
param spaClientId string
param apiClientId string
param allowedGroupId string
@secure()
param workIqClientSecret string
param oauthCredentialExpiresOn string

var suffix = take(toLower(uniqueString(subscription().id, resourceGroup().id, name, location)), 8)
var compactName = take(replace(toLower(name), '-', ''), 10)
var functionAppName = 'func-${compactName}-${suffix}'
var functionIdentityName = 'id-${compactName}-${suffix}'
var storageAccountName = take('st${compactName}${suffix}', 24)
var deploymentContainerName = 'deploymentpackage'
var staticWebAppName = 'stapp-${compactName}-${suffix}'
var logAnalyticsName = 'log-${compactName}-${suffix}'
var applicationInsightsName = 'appi-${compactName}-${suffix}'
var keyVaultName = take('kv-${compactName}-${suffix}', 24)
var foundryAccountName = take('aif-${compactName}-${suffix}', 64)
var foundryProjectName = take('project-${compactName}', 64)
var modelDeploymentName = 'gpt-4.1'
var agentName = 'workiq-profile-agent'
var storageTags = union(tags, {
  SecurityControl: 'Ignore'
})

module logAnalytics 'br/public:avm/res/operational-insights/workspace:0.11.1' = {
  name: 'log-analytics'
  params: {
    name: logAnalyticsName
    location: location
    tags: tags
    dataRetention: 30
  }
}

module applicationInsights 'br/public:avm/res/insights/component:0.6.0' = {
  name: 'application-insights'
  params: {
    name: applicationInsightsName
    location: location
    tags: tags
    workspaceResourceId: logAnalytics.outputs.resourceId
    disableLocalAuth: false
  }
}

module functionIdentity 'br/public:avm/res/managed-identity/user-assigned-identity:0.4.1' = {
  name: 'function-identity'
  params: {
    name: functionIdentityName
    location: location
    tags: tags
  }
}

module storage 'br/public:avm/res/storage/storage-account:0.8.3' = {
  name: 'function-storage'
  params: {
    name: storageAccountName
    location: location
    tags: storageTags
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    minimumTlsVersion: 'TLS1_2'
    publicNetworkAccess: 'Enabled'
    networkAcls: {
      bypass: 'AzureServices'
      defaultAction: 'Allow'
    }
    blobServices: {
      containers: [
        {
          name: deploymentContainerName
        }
      ]
    }
  }
}

module keyVault 'br/public:avm/res/key-vault/vault:0.14.0' = {
  name: 'key-vault'
  params: {
    name: keyVaultName
    location: location
    tags: tags
    sku: 'standard'
    enableRbacAuthorization: true
    enablePurgeProtection: true
    publicNetworkAccess: 'Enabled'
    networkAcls: {
      bypass: 'AzureServices'
      defaultAction: 'Allow'
      ipRules: []
      virtualNetworkRules: []
    }
  }
}

module staticWebApp 'br/public:avm/res/web/static-site:0.9.0' = {
  name: 'static-web-app'
  params: {
    name: staticWebAppName
    location: location
    sku: 'Standard'
    publicNetworkAccess: 'Enabled'
    tags: union(tags, {
      'azd-service-name': 'web'
    })
  }
}

module functionApp './function-app.bicep' = {
  name: 'function-app'
  params: {
    name: functionAppName
    location: location
    tags: tags
    tenantId: tenantId
    spaClientId: spaClientId
    apiClientId: apiClientId
    managedIdentityResourceId: functionIdentity.outputs.resourceId
    managedIdentityClientId: functionIdentity.outputs.clientId
    appInsightsConnectionString: applicationInsights.outputs.connectionString
    storageBlobEndpoint: storage.outputs.primaryBlobEndpoint
    deploymentStorageContainerName: deploymentContainerName
    staticWebAppOrigin: 'https://${staticWebApp.outputs.defaultHostname}'
    oauthCredentialExpiresOn: oauthCredentialExpiresOn
    foundryProjectEndpoint: 'https://${foundryAccountName}.services.ai.azure.com/api/projects/${foundryProjectName}'
    foundryAgentName: agentName
  }
}

module foundryAccount 'br/public:avm/res/cognitive-services/account:0.19.0' = {
  name: 'foundry-account'
  params: {
    name: foundryAccountName
    location: location
    tags: tags
    kind: 'AIServices'
    sku: 'S0'
    customSubDomainName: foundryAccountName
    allowProjectManagement: true
    managedIdentities: {
      systemAssigned: true
    }
    disableLocalAuth: true
    publicNetworkAccess: 'Enabled'
    deployments: [
      {
        name: modelDeploymentName
        model: {
          format: 'OpenAI'
          name: 'gpt-4.1'
        }
        sku: {
          name: 'GlobalStandard'
          capacity: 10
        }
        versionUpgradeOption: 'OnceNewDefaultVersionAvailable'
      }
    ]
  }
}

resource foundryAccountResource 'Microsoft.CognitiveServices/accounts@2025-06-01' existing = {
  name: foundryAccountName
}

resource foundryProject 'Microsoft.CognitiveServices/accounts/projects@2025-06-01' = {
  parent: foundryAccountResource
  name: foundryProjectName
  location: location
  tags: tags
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    displayName: 'Work IQ Profile Chat'
    description: 'Grounded Microsoft 365 profile and recent activity chat.'
  }
  dependsOn: [
    foundryAccount
  ]
}

resource appInsightsResource 'Microsoft.Insights/components@2020-02-02' existing = {
  name: applicationInsightsName
}

var storageBlobDataOwnerRoleId = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  'b7e6dc6d-f1e8-4753-8033-0f276bb0955b'
)
var foundryUserRoleId = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  '53ca6127-db72-4b80-b1b0-d745d6d5456d'
)
var monitoringMetricsPublisherRoleId = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  '3913510d-42f4-4e42-8a64-420c390055eb'
)
var foundryAgentConsumerRoleId = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  'eed3b665-ab3a-47b6-8f48-c9382fb1dad6'
)
var logAnalyticsReaderRoleId = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  '73c42c96-874c-492b-b04d-ab87d138a893'
)
var privilegedMonitoringDataReaderRoleId = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  'dbc9c667-e97f-4491-aee6-90b9cf960190'
)

resource storageAccountResource 'Microsoft.Storage/storageAccounts@2023-05-01' existing = {
  name: storageAccountName
}

resource keyVaultResource 'Microsoft.KeyVault/vaults@2024-11-01' existing = {
  name: keyVaultName
}

resource workIqSecret 'Microsoft.KeyVault/vaults/secrets@2025-05-01' = {
  parent: keyVaultResource
  name: 'workiq-oauth-client-secret'
  properties: {
    value: workIqClientSecret
    contentType: 'Work IQ OAuth client secret'
    attributes: {
      enabled: true
      exp: dateTimeToEpoch(oauthCredentialExpiresOn)
    }
  }
  dependsOn: [
    keyVault
  ]
}

resource functionStorageRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccountResource.id, functionIdentityName, storageBlobDataOwnerRoleId)
  scope: storageAccountResource
  properties: {
    principalId: functionIdentity.outputs.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: storageBlobDataOwnerRoleId
  }
  dependsOn: [
    storage
  ]
}

resource functionMonitoringRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(appInsightsResource.id, functionIdentityName, monitoringMetricsPublisherRoleId)
  scope: appInsightsResource
  properties: {
    principalId: functionIdentity.outputs.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: monitoringMetricsPublisherRoleId
  }
  dependsOn: [
    applicationInsights
  ]
}

resource groupFoundryRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(foundryProject.id, allowedGroupId, foundryAgentConsumerRoleId)
  scope: foundryProject
  properties: {
    principalId: allowedGroupId
    principalType: 'Group'
    roleDefinitionId: foundryAgentConsumerRoleId
  }
}

resource deployerFoundryRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(foundryProject.id, deployerPrincipalId, foundryUserRoleId)
  scope: foundryProject
  properties: {
    principalId: deployerPrincipalId
    principalType: 'User'
    roleDefinitionId: foundryUserRoleId
  }
}

resource projectLogReaderRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(appInsightsResource.id, foundryProject.id, logAnalyticsReaderRoleId)
  scope: appInsightsResource
  properties: {
    principalId: foundryProject.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: logAnalyticsReaderRoleId
  }
  dependsOn: [
    applicationInsights
  ]
}

resource projectPrivilegedLogReaderRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(appInsightsResource.id, foundryProject.id, privilegedMonitoringDataReaderRoleId)
  scope: appInsightsResource
  properties: {
    principalId: foundryProject.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: privilegedMonitoringDataReaderRoleId
  }
  dependsOn: [
    applicationInsights
  ]
}

output functionAppName string = functionApp.outputs.name
output functionIdentityClientId string = functionIdentity.outputs.clientId
output functionIdentityPrincipalId string = functionIdentity.outputs.principalId
output staticWebAppName string = staticWebApp.outputs.name
output apiUrl string = 'https://${functionApp.outputs.defaultHostname}'
output webUrl string = 'https://${staticWebApp.outputs.defaultHostname}'
output keyVaultName string = keyVault.outputs.name
output applicationInsightsName string = applicationInsights.outputs.name
output logAnalyticsWorkspaceId string = logAnalytics.outputs.resourceId
output foundryAccountName string = foundryAccount.outputs.name
output foundryProjectName string = foundryProject.name
output foundryProjectEndpoint string = 'https://${foundryAccountName}.services.ai.azure.com/api/projects/${foundryProject.name}'
output foundryProjectId string = foundryProject.id
