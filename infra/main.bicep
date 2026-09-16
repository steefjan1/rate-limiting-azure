targetScope = 'subscription'

@minLength(1)
@maxLength(64)
@description('Name of the azd environment; used to derive resource names.')
param environmentName string

@minLength(1)
@description('Primary location for all resources. azd prompts for AZURE_LOCATION.')
param location string

@description('Deploy Azure API Management (Basic v2). Costs money while running; set false to skip the gateway layer.')
param deployApim bool = true

@description('Deploy Azure Managed Redis (Balanced B0) and wire it into the API for the distributed limiters.')
param deployRedis bool = true

@description('Publisher email for API Management.')
param apimPublisherEmail string = 'admin@example.com'

@description('Publisher name for API Management.')
param apimPublisherName string = 'Cloud Perspectives'

var tags = { 'azd-env-name': environmentName }
var resourceToken = toLower(uniqueString(subscription().id, environmentName, location))

resource rg 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: 'rg-${environmentName}'
  location: location
  tags: tags
}

module monitoring 'modules/monitoring.bicep' = {
  name: 'monitoring'
  scope: rg
  params: {
    location: location
    tags: tags
    resourceToken: resourceToken
  }
}

module redis 'modules/redis.bicep' = if (deployRedis) {
  name: 'redis'
  scope: rg
  params: {
    location: location
    tags: tags
    resourceToken: resourceToken
  }
}

module api 'modules/containerapp.bicep' = {
  name: 'api'
  scope: rg
  params: {
    location: location
    tags: tags
    resourceToken: resourceToken
    logAnalyticsWorkspaceId: monitoring.outputs.logAnalyticsWorkspaceId
    redisConnectionString: redis.?outputs.connectionString ?? ''
  }
}

module apim 'modules/apim.bicep' = if (deployApim) {
  name: 'apim'
  scope: rg
  params: {
    location: location
    tags: tags
    resourceToken: resourceToken
    publisherEmail: apimPublisherEmail
    publisherName: apimPublisherName
    backendFqdn: api.outputs.fqdn
    logAnalyticsWorkspaceId: monitoring.outputs.logAnalyticsWorkspaceId
  }
}

output AZURE_LOCATION string = location
output AZURE_RESOURCE_GROUP string = rg.name
output AZURE_CONTAINER_REGISTRY_ENDPOINT string = api.outputs.registryLoginServer
output API_BASE_URL string = 'https://${api.outputs.fqdn}'
output APIM_GATEWAY_URL string = apim.?outputs.gatewayUrl ?? ''
output APIM_API_URL string = deployApim ? '${apim.?outputs.gatewayUrl ?? ''}/ratelimit' : ''
output REDIS_HOST string = redis.?outputs.hostName ?? ''
