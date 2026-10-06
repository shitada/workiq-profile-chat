targetScope = 'subscription'

param resourceGroupName string
param functionAppName string
param functionSubnetResourceId string

@description('Reviewed current writable Storage state, without keys or read-only properties.')
@secure()
param storageStateBase64 string

@description('Reviewed current writable Key Vault state, without secrets or read-only properties.')
@secure()
param vaultStateBase64 string

resource resourceGroup 'Microsoft.Resources/resourceGroups@2024-11-01' existing = {
  name: resourceGroupName
}

module cutover '../../../infra/modules/private-cutover.bicep' = {
  name: 'workiq-profile-chat-private-cutover'
  scope: resourceGroup
  params: {
    functionAppName: functionAppName
    functionSubnetResourceId: functionSubnetResourceId
    storageStateBase64: storageStateBase64
    vaultStateBase64: vaultStateBase64
  }
}
