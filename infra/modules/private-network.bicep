targetScope = 'resourceGroup'

param name string
param location string
param tags object = {}

@description('IPAM-approved address space for the application VNet.')
@minLength(1)
param addressPrefix string

@description('Dedicated Flex integration subnet, /27 or larger; /26 is recommended.')
@minLength(1)
param functionSubnetPrefix string

@description('Non-overlapping subnet for private endpoints.')
@minLength(1)
param privateEndpointSubnetPrefix string

param storageAccountName string
param keyVaultName string

var suffix = take(toLower(uniqueString(subscription().id, resourceGroup().id, name, location)), 8)
var compactName = take(replace(toLower(name), '-', ''), 10)
var networkName = 'vnet-${compactName}-${suffix}'
var functionSubnetName = 'functions'
var privateEndpointSubnetName = 'private-endpoints'

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' existing = {
  name: storageAccountName
}

resource vault 'Microsoft.KeyVault/vaults@2024-11-01' existing = {
  name: keyVaultName
}

resource network 'Microsoft.Network/virtualNetworks@2024-05-01' = {
  name: networkName
  location: location
  tags: tags
  properties: {
    addressSpace: {
      addressPrefixes: [
        addressPrefix
      ]
    }
    subnets: [
      {
        name: functionSubnetName
        properties: {
          addressPrefix: functionSubnetPrefix
          delegations: [
            {
              name: 'flex-consumption'
              properties: {
                serviceName: 'Microsoft.App/environments'
              }
            }
          ]
        }
      }
      {
        name: privateEndpointSubnetName
        properties: {
          addressPrefix: privateEndpointSubnetPrefix
          privateEndpointNetworkPolicies: 'Disabled'
        }
      }
    ]
  }
}

var privateServices = [
  {
    name: 'blob'
    resourceId: storage.id
    zone: 'privatelink.blob.${environment().suffixes.storage}'
  }
  {
    name: 'vault'
    resourceId: vault.id
    zone: 'privatelink.vaultcore.azure.net'
  }
]

resource dnsZones 'Microsoft.Network/privateDnsZones@2020-06-01' = [for service in privateServices: {
  name: service.zone
  location: 'global'
  tags: tags
}]

resource dnsLinks 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2020-06-01' = [for (service, index) in privateServices: {
  parent: dnsZones[index]
  name: networkName
  location: 'global'
  properties: {
    registrationEnabled: false
    virtualNetwork: {
      id: network.id
    }
  }
}]

resource endpoints 'Microsoft.Network/privateEndpoints@2024-05-01' = [for service in privateServices: {
  name: 'pe-${compactName}-${suffix}-${service.name}'
  location: location
  tags: tags
  properties: {
    subnet: {
      id: resourceId('Microsoft.Network/virtualNetworks/subnets', networkName, privateEndpointSubnetName)
    }
    privateLinkServiceConnections: [
      {
        name: service.name
        properties: {
          privateLinkServiceId: service.resourceId
          groupIds: [
            service.name
          ]
        }
      }
    ]
  }
  dependsOn: [
    network
  ]
}]

resource dnsZoneGroups 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2024-05-01' = [for (service, index) in privateServices: {
  parent: endpoints[index]
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: service.name
        properties: {
          privateDnsZoneId: dnsZones[index].id
        }
      }
    ]
  }
}]

output virtualNetworkName string = network.name
output virtualNetworkResourceId string = network.id
output functionSubnetResourceId string = resourceId('Microsoft.Network/virtualNetworks/subnets', networkName, functionSubnetName)
output privateEndpointNames array = [for (service, index) in privateServices: endpoints[index].name]
