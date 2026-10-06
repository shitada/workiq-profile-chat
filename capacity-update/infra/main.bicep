targetScope = 'subscription'

param resourceGroupName string
param accountName string
param capacity int = 200

resource rg 'Microsoft.Resources/resourceGroups@2024-11-01' existing = {
  name: resourceGroupName
}

module capacityUpdate '../../infra/model-capacity.bicep' = {
  name: 'report-model-capacity'
  scope: rg
  params: {
    accountName: accountName
    capacity: capacity
  }
}
