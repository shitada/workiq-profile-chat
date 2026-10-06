targetScope = 'resourceGroup'

param name string
param location string = resourceGroup().location
param tags object = {}
param tenantId string
param spaClientId string
param apiClientId string
param managedIdentityResourceId string
param managedIdentityClientId string
param appInsightsConnectionString string
param storageBlobEndpoint string
param deploymentStorageContainerName string
param staticWebAppOrigin string
param oauthCredentialExpiresOn string
param foundryProjectEndpoint string
param foundryAgentName string
param reportAgentVersion string
param reportCollectorVersion string
param reportSharePointDriveId string
param reportSharePointFolderId string
param virtualNetworkSubnetId string

var serviceName = 'api'
var planName = 'plan-${name}'
var applicationInsightsIdentity = 'ClientId=${managedIdentityClientId};Authorization=AAD'

module plan 'br/public:avm/res/web/serverfarm:0.1.1' = {
  name: 'flex-consumption-plan'
  params: {
    name: planName
    location: location
    tags: tags
    reserved: true
    sku: {
      name: 'FC1'
      tier: 'FlexConsumption'
    }
  }
}

module app 'br/public:avm/res/web/site:0.15.1' = {
  name: 'function-site'
  params: {
    name: name
    location: location
    kind: 'functionapp,linux'
    tags: union(tags, {
      'azd-service-name': serviceName
    })
    serverFarmResourceId: plan.outputs.resourceId
    virtualNetworkSubnetId: virtualNetworkSubnetId
    managedIdentities: {
      systemAssigned: false
      userAssignedResourceIds: [
        managedIdentityResourceId
      ]
    }
    functionAppConfig: {
      deployment: {
        storage: {
          type: 'blobContainer'
          value: '${storageBlobEndpoint}${deploymentStorageContainerName}'
          authentication: {
            type: 'UserAssignedIdentity'
            userAssignedIdentityResourceId: managedIdentityResourceId
          }
        }
      }
      scaleAndConcurrency: {
        instanceMemoryMB: 2048
        maximumInstanceCount: 20
      }
      runtime: {
        name: 'dotnet-isolated'
        version: '10.0'
      }
    }
    siteConfig: {
      alwaysOn: false
      http20Enabled: true
      minTlsVersion: '1.2'
      cors: {
        allowedOrigins: [
          staticWebAppOrigin
          'http://localhost:5173'
        ]
        supportCredentials: false
      }
    }
    appSettingsKeyValuePairs: {
      AzureWebJobsStorage__blobServiceUri: storageBlobEndpoint
      AzureWebJobsStorage__credential: 'managedidentity'
      AzureWebJobsStorage__clientId: managedIdentityClientId
      APPLICATIONINSIGHTS_AUTHENTICATION_STRING: applicationInsightsIdentity
      APPLICATIONINSIGHTS_CONNECTION_STRING: appInsightsConnectionString
      FUNCTIONS_EXTENSION_VERSION: '~4'
      Authentication__TenantId: tenantId
      Authentication__ApiClientId: apiClientId
      Authentication__RequiredRole: 'ProfileChat.User'
      Authentication__ManagedIdentityClientId: managedIdentityClientId
      Foundry__ProjectEndpoint: foundryProjectEndpoint
      Foundry__AgentName: foundryAgentName
      Foundry__AgentVersion: reportAgentVersion
      Foundry__CollectorAgentName: 'workiq-report-collector'
      Foundry__CollectorAgentVersion: reportCollectorVersion
      Report__Provider: 'workiq'
      Report__StorageServiceUri: storageBlobEndpoint
      Report__SharePointDriveId: reportSharePointDriveId
      Report__SharePointFolderId: reportSharePointFolderId
      Report__TestYear: '2026'
      Foundry__Scope: 'https://ai.azure.com/.default'
      Authentication__WorkIqClientSecretExpiresOn: oauthCredentialExpiresOn
      OTEL_SERVICE_NAME: 'workiq-profile-chat-api'
      OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT: 'false'
    }
  }
}

resource functionSite 'Microsoft.Web/sites@2024-11-01' existing = {
  name: name
}

resource authSettings 'Microsoft.Web/sites/config@2024-11-01' = {
  parent: functionSite
  name: 'authsettingsV2'
  properties: any({
    platform: {
      enabled: true
      runtimeVersion: '~1'
    }
    globalValidation: {
      requireAuthentication: true
      unauthenticatedClientAction: 'Return401'
      excludedPaths: [
        '/api/health'
      ]
    }
    identityProviders: {
      azureActiveDirectory: {
        enabled: true
        registration: {
          clientId: apiClientId
          openIdIssuer: '${environment().authentication.loginEndpoint}${tenantId}/v2.0'
        }
        validation: {
          allowedAudiences: [
            apiClientId
            'api://${apiClientId}'
          ]
          defaultAuthorizationPolicy: {
            allowedApplications: [
              spaClientId
            ]
          }
        }
      }
    }
    login: {
      tokenStore: {
        enabled: true
      }
      preserveUrlFragmentsForLogins: false
    }
    httpSettings: {
      requireHttps: true
      routes: {
        apiPrefix: '/.auth'
      }
    }
  })
  dependsOn: [
    app
  ]
}

output name string = app.outputs.name
output defaultHostname string = app.outputs.defaultHostname
