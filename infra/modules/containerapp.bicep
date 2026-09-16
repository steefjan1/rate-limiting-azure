param location string
param tags object
param resourceToken string
param logAnalyticsWorkspaceId string

@secure()
param redisConnectionString string = ''

@description('Image to run. azd replaces this on `azd deploy`; the placeholder keeps the first provision green.')
param image string = 'mcr.microsoft.com/azuredocs/containerapps-helloworld:latest'

@description('Two replicas on purpose: it makes the in-process limiters visibly wrong and the Redis ones visibly right.')
param minReplicas int = 2
param maxReplicas int = 2

resource registry 'Microsoft.ContainerRegistry/registries@2023-11-01-preview' = {
  #disable-next-line BCP334 // uniqueString is always 13 characters
  name: 'cr${resourceToken}'
  location: location
  tags: tags
  sku: { name: 'Basic' }
  properties: {
    adminUserEnabled: false
  }
}

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-api-${resourceToken}'
  location: location
  tags: tags
}

// AcrPull
resource acrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(registry.id, identity.id, '7f951dda-4ed3-4680-a7ca-43fe172d538d')
  scope: registry
  properties: {
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
  }
}

resource environment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: 'cae-${resourceToken}'
  location: location
  tags: tags
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: reference(logAnalyticsWorkspaceId, '2023-09-01').customerId
        sharedKey: listKeys(logAnalyticsWorkspaceId, '2023-09-01').primarySharedKey
      }
    }
  }
}

resource app 'Microsoft.App/containerApps@2024-03-01' = {
  name: 'ca-api-${resourceToken}'
  location: location
  tags: union(tags, { 'azd-service-name': 'api' })
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${identity.id}': {} }
  }
  dependsOn: [acrPull]
  properties: {
    managedEnvironmentId: environment.id
    configuration: {
      ingress: {
        external: true
        targetPort: 80 // the placeholder image and the API both listen on 80
        transport: 'http'
      }
      registries: [
        {
          server: registry.properties.loginServer
          identity: identity.id
        }
      ]
      secrets: empty(redisConnectionString) ? [] : [
        {
          name: 'redis-connection'
          value: redisConnectionString
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'api'
          image: image
          resources: {
            cpu: json('0.25')
            memory: '0.5Gi'
          }
          env: concat(
            [
              { name: 'ASPNETCORE_URLS', value: 'http://+:80' }
              { name: 'RateLimits__PermitLimit', value: '10' }
              { name: 'RateLimits__WindowMs', value: '1000' }
            ],
            empty(redisConnectionString) ? [] : [
              { name: 'ConnectionStrings__Redis', secretRef: 'redis-connection' }
            ]
          )
        }
      ]
      scale: {
        minReplicas: minReplicas
        maxReplicas: maxReplicas
      }
    }
  }
}

output fqdn string = app.properties.configuration.ingress.fqdn
output registryLoginServer string = registry.properties.loginServer
output name string = app.name
