targetScope = 'resourceGroup'

param functionAppName string
param functionSubnetResourceId string

@secure()
param storageStateBase64 string

@secure()
param vaultStateBase64 string

var storageState = json(base64ToString(storageStateBase64))
var vaultState = json(base64ToString(vaultStateBase64))

resource functionApp 'Microsoft.Web/sites@2024-11-01' existing = {
  name: functionAppName
}

resource integration 'Microsoft.Web/sites/networkConfig@2024-11-01' = {
  parent: functionApp
  name: 'virtualNetwork'
  properties: {
    subnetResourceId: functionSubnetResourceId
  }
}

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageState.name
  location: storageState.location
  kind: storageState.kind
  sku: storageState.sku
  tags: toObject(filter(items(storageState.tags), tag => toLower(tag.key) != 'securitycontrol'), tag => tag.key, tag => tag.value)
  properties: union(storageState.properties, {
    publicNetworkAccess: 'Disabled'
    networkAcls: {
      bypass: 'None'
      defaultAction: 'Deny'
      ipRules: []
      virtualNetworkRules: []
      resourceAccessRules: []
    }
  })
  dependsOn: [
    integration
  ]
}

resource vault 'Microsoft.KeyVault/vaults@2024-11-01' = {
  name: vaultState.name
  location: vaultState.location
  tags: toObject(filter(items(vaultState.tags), tag => toLower(tag.key) != 'securitycontrol'), tag => tag.key, tag => tag.value)
  properties: union(vaultState.properties, {
    publicNetworkAccess: 'Disabled'
    enabledForDeployment: false
    enabledForDiskEncryption: false
    enabledForTemplateDeployment: false
    networkAcls: {
      bypass: 'None'
      defaultAction: 'Deny'
      ipRules: []
      virtualNetworkRules: []
    }
  })
  dependsOn: [
    integration
  ]
}
