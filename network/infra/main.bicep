targetScope = 'subscription'

@description('Use the original application environment name, not the bootstrap environment name.')
@minLength(1)
param projectEnvironmentName string

param location string
param resourceGroupName string
param storageAccountName string
param keyVaultName string
param networkAddressPrefix string
param functionSubnetPrefix string
param privateEndpointSubnetPrefix string

resource resourceGroup 'Microsoft.Resources/resourceGroups@2024-11-01' existing = {
  name: resourceGroupName
}

module network '../../infra/modules/private-network.bicep' = {
  name: 'workiq-profile-chat-network'
  scope: resourceGroup
  params: {
    name: projectEnvironmentName
    location: location
    tags: {
      'azd-env-name': projectEnvironmentName
      application: 'workiq-profile-chat'
      dataClassification: 'confidential'
    }
    addressPrefix: networkAddressPrefix
    functionSubnetPrefix: functionSubnetPrefix
    privateEndpointSubnetPrefix: privateEndpointSubnetPrefix
    storageAccountName: storageAccountName
    keyVaultName: keyVaultName
  }
}

output AZURE_VIRTUAL_NETWORK_NAME string = network.outputs.virtualNetworkName
output AZURE_VIRTUAL_NETWORK_ID string = network.outputs.virtualNetworkResourceId
output AZURE_FUNCTION_SUBNET_ID string = network.outputs.functionSubnetResourceId
output AZURE_PRIVATE_ENDPOINT_NAMES array = network.outputs.privateEndpointNames
