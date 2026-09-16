param location string
param tags object
param resourceToken string
param publisherEmail string
param publisherName string
param backendFqdn string
param logAnalyticsWorkspaceId string

resource apim 'Microsoft.ApiManagement/service@2024-05-01' = {
  name: 'apim-${resourceToken}'
  location: location
  tags: tags
  sku: {
    name: 'BasicV2'
    capacity: 1
  }
  properties: {
    publisherEmail: publisherEmail
    publisherName: publisherName
  }
}

resource api 'Microsoft.ApiManagement/service/apis@2024-05-01' = {
  parent: apim
  name: 'ratelimit'
  properties: {
    displayName: 'Rate limiting sample'
    description: 'Six rate limiting algorithms behind one gateway.'
    path: 'ratelimit'
    protocols: ['https']
    serviceUrl: 'https://${backendFqdn}'
    subscriptionRequired: true
    subscriptionKeyParameterNames: {
      header: 'Ocp-Apim-Subscription-Key'
      query: 'subscription-key'
    }
  }
}

// One wildcard operation forwards every GET under /ratelimit to the same path on the backend.
resource getAll 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = {
  parent: api
  name: 'get-all'
  properties: {
    displayName: 'GET anything'
    method: 'GET'
    urlTemplate: '/*'
  }
}

resource apiPolicy 'Microsoft.ApiManagement/service/apis/policies@2024-05-01' = {
  parent: api
  name: 'policy'
  properties: {
    format: 'rawxml'
    value: loadTextContent('../apim/policies/api-policy.xml')
  }
}

resource product 'Microsoft.ApiManagement/service/products@2024-05-01' = {
  parent: apim
  name: 'ratelimit-demo'
  properties: {
    displayName: 'Rate limiting demo'
    subscriptionRequired: true
    approvalRequired: false
    state: 'published'
  }
}

resource productApi 'Microsoft.ApiManagement/service/products/apis@2024-05-01' = {
  parent: product
  name: api.name
}

resource subscription 'Microsoft.ApiManagement/service/subscriptions@2024-05-01' = {
  parent: apim
  name: 'demo-client'
  properties: {
    displayName: 'demo-client'
    scope: product.id
    state: 'active'
  }
}

// Gateway request logs to Log Analytics: one row per request with ResponseCode,
// BackendResponseCode, TotalTime and BackendTime. This is what separates
// "the gateway rejected it" from "the service rejected it" in docs/kql.md.
resource diagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'to-log-analytics'
  scope: apim
  properties: {
    workspaceId: logAnalyticsWorkspaceId
    // Dedicated = resource-specific table (ApiManagementGatewayLogs) with typed columns.
    // Without it, rows land in the legacy AzureDiagnostics table as responseCode_d etc.
    logAnalyticsDestinationType: 'Dedicated'
    logs: [
      {
        category: 'GatewayLogs'
        enabled: true
      }
    ]
  }
}

output gatewayUrl string = apim.properties.gatewayUrl
output subscriptionName string = subscription.name
