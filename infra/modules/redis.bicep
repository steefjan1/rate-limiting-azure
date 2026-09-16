// Azure Managed Redis. Azure Cache for Redis is retiring (Basic/Standard/Premium on
// 30 September 2028) and closes to new creations for existing customers from
// 1 October 2026, so new samples should start here.
param location string
param tags object
param resourceToken string

@description('Balanced_B0 is the smallest SKU. Good enough for a rate-limit counter.')
param skuName string = 'Balanced_B0'

resource redis 'Microsoft.Cache/redisEnterprise@2025-07-01' = {
  name: 'redis-${resourceToken}'
  location: location
  tags: tags
  sku: {
    name: skuName
  }
  properties: {
    minimumTlsVersion: '1.2'
    // Sample setting. Keep 'Enabled' (the default) for anything you rely on.
    highAvailability: 'Disabled'
    publicNetworkAccess: 'Enabled'
  }
}

resource database 'Microsoft.Cache/redisEnterprise/databases@2025-07-01' = {
  parent: redis
  name: 'default'
  properties: {
    port: 10000
    clientProtocol: 'Encrypted'
    // Enterprise clustering hides the cluster behind one endpoint, so StackExchange.Redis
    // needs no cluster awareness. The Lua scripts still use hash tags so they would also
    // work under OSS clustering.
    clusteringPolicy: 'EnterpriseCluster'
    evictionPolicy: 'NoEviction'
    // Access keys keep the sample short. Production: disable keys, use Entra ID with
    // Microsoft.Azure.StackExchangeRedis and a managed identity.
    accessKeysAuthentication: 'Enabled'
  }
}

output hostName string = redis.properties.hostName
#disable-next-line outputs-should-not-contain-secrets // sample: key flows straight into a Container Apps secret
output connectionString string = '${redis.properties.hostName}:10000,password=${database.listKeys().primaryKey},ssl=True,abortConnect=False'
