targetScope = 'resourceGroup'

@description('Globally unique Key Vault name. It must contain only lowercase letters, numbers, and hyphens.')
param keyVaultName string

@description('Name of the Linux App Service plan.')
param appServicePlanName string

@description('Name of the App Service hosting the trading platform.')
param appName string

@description('Name of the Log Analytics workspace used by Application Insights.')
param logAnalyticsWorkspaceName string

@description('Name of the Application Insights component.')
param applicationInsightsName string

@secure()
@description('Azure SQL connection string. It is supplied by the deployment pipeline, never committed.')
param tradingDbConnectionString string

@description('The resource ID of the action group that receives live-trading anomaly alerts.')
param alertActionGroupResourceId string

@description('Whether this deployment may register a Kraken route capable of real orders. Defaults to false.')
param krakenLiveExecutionEnabled bool = false

@minValue(1)
@description('Mandatory maximum notional for every live order. User settings can only be stricter.')
param maxOrderNotional int = 100

@minValue(1)
@description('Maximum notional for a first real proving order.')
param defaultProvingNotionalCeiling int = 25

@description('Kraken symbols permitted during the proving stage. An empty array blocks all proving orders.')
param provingSymbols array = []

@description('Object IDs of users in the explicitly approved initial live-trading cohort. An empty array permits nobody.')
param allowedUserIds array = []

param location string = resourceGroup().location

var keyVaultSecretsOfficerRoleDefinitionId = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  'b86a8fe4-44ce-4948-aee5-eccb2c155cd7'
)

var liveSymbolSettings = [
  for (symbol, index) in provingSymbols: {
    name: 'Trading__LiveExecution__ProvingSymbols__${index}'
    value: string(symbol)
  }
]

var liveCohortSettings = [
  for (userId, index) in allowedUserIds: {
    name: 'Trading__LiveExecution__AllowedUserIds__${index}'
    value: string(userId)
  }
]

resource workspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: logAnalyticsWorkspaceName
  location: location
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
  }
}

resource applicationInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: applicationInsightsName
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: workspace.id
  }
}

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: keyVaultName
  location: location
  properties: {
    sku: {
      family: 'A'
      name: 'standard'
    }
    tenantId: subscription().tenantId
    enableRbacAuthorization: true
    enablePurgeProtection: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 90
    publicNetworkAccess: 'Enabled'
  }
}

resource appServicePlan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: appServicePlanName
  location: location
  sku: {
    name: 'P0v3'
    tier: 'PremiumV3'
  }
  kind: 'linux'
  properties: {
    reserved: true
  }
}

resource appService 'Microsoft.Web/sites@2023-12-01' = {
  name: appName
  location: location
  kind: 'app,linux'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    httpsOnly: true
    serverFarmId: appServicePlan.id
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|8.0'
      alwaysOn: true
      minTlsVersion: '1.2'
      appSettings: concat([
        {
          name: 'ASPNETCORE_ENVIRONMENT'
          value: 'Production'
        }
        {
          name: 'ConnectionStrings__TradingDb'
          value: tradingDbConnectionString
        }
        {
          name: 'KeyVault__Uri'
          value: keyVault.properties.vaultUri
        }
        {
          name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
          value: applicationInsights.properties.ConnectionString
        }
        {
          name: 'Trading__LiveExecution__Kraken__Enabled'
          value: string(krakenLiveExecutionEnabled)
        }
        {
          name: 'Trading__LiveExecution__MaxOrderNotional'
          value: string(maxOrderNotional)
        }
        {
          name: 'Trading__LiveExecution__DefaultProvingNotionalCeiling'
          value: string(defaultProvingNotionalCeiling)
        }
      ], liveSymbolSettings, liveCohortSettings)
    }
  }
}

resource appServiceKeyVaultSecretsOfficer 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, appService.id, keyVaultSecretsOfficerRoleDefinitionId)
  scope: keyVault
  properties: {
    principalId: appService.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: keyVaultSecretsOfficerRoleDefinitionId
  }
}

resource unknownLiveOrdersAlert 'Microsoft.Insights/scheduledQueryRules@2023-12-01' = {
  name: '${appName}-live-order-unknown'
  location: location
  properties: {
    displayName: '${appName}: live order outcome unknown'
    description: 'A Kraken submission did not establish whether an order exists. Immediate reconciliation is required.'
    severity: 1
    enabled: true
    evaluationFrequency: 'PT5M'
    windowSize: 'PT5M'
    scopes: [
      applicationInsights.id
    ]
    criteria: {
      allOf: [
        {
          query: 'traces | where message has ''LiveOrderUnknown'' | summarize Count = count() by bin(timestamp, 5m)'
          timeAggregation: 'Count'
          metricMeasureColumn: 'Count'
          operator: 'GreaterThan'
          threshold: 0
          failingPeriods: {
            numberOfEvaluationPeriods: 1
            minFailingPeriodsToAlert: 1
          }
        }
      ]
    }
    actions: {
      actionGroups: [
        alertActionGroupResourceId
      ]
    }
  }
}

resource rejectedLiveOrdersAlert 'Microsoft.Insights/scheduledQueryRules@2023-12-01' = {
  name: '${appName}-live-order-rejected'
  location: location
  properties: {
    displayName: '${appName}: Kraken live-order rejections'
    description: 'Three or more exchange rejections in five minutes usually indicate a stale instrument filter, incorrect account configuration, or a defect.'
    severity: 2
    enabled: true
    evaluationFrequency: 'PT5M'
    windowSize: 'PT5M'
    scopes: [
      applicationInsights.id
    ]
    criteria: {
      allOf: [
        {
          query: 'traces | where message has ''LiveOrderRejected'' | summarize Count = count() by bin(timestamp, 5m)'
          timeAggregation: 'Count'
          metricMeasureColumn: 'Count'
          operator: 'GreaterThanOrEqual'
          threshold: 3
          failingPeriods: {
            numberOfEvaluationPeriods: 1
            minFailingPeriodsToAlert: 1
          }
        }
      ]
    }
    actions: {
      actionGroups: [
        alertActionGroupResourceId
      ]
    }
  }
}

output appServiceManagedIdentityPrincipalId string = appService.identity.principalId
output keyVaultUri string = keyVault.properties.vaultUri
output applicationInsightsConnectionString string = applicationInsights.properties.ConnectionString
