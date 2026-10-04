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

@description('Globally unique name for the private historical-research Blob account. Lowercase letters and numbers only.')
param historicalArchiveStorageAccountName string = 'hist${uniqueString(resourceGroup().id)}'

@secure()
@description('Azure SQL connection string. It is supplied by the deployment pipeline, never committed.')
param tradingDbConnectionString string

@description('The resource ID of the action group that receives live-trading anomaly alerts.')
param alertActionGroupResourceId string

@description('Whether this deployment may register a Kraken route capable of real orders. Defaults to false.')
param krakenLiveExecutionEnabled bool = false

@description('Opt in to both continuously supervised paper worker hosts after the SQL schema, feed, and recovery procedures have been verified. Does not enable live trading.')
param paperWorkerHostsEnabled bool = false

@description('Opt in to active plan/trial worker limits after owner assignments are provisioned in SQL. Set consistently for web and both paper hosts; defaults to legacy paper capacity.')
param enforcePaperWorkerEntitlements bool = false

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

var blobDataContributorRoleDefinitionId = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
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

resource historicalArchiveStorage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: historicalArchiveStorageAccountName
  location: location
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    publicNetworkAccess: 'Enabled'
  }
}

resource historicalArchiveBlobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: historicalArchiveStorage
  name: 'default'
  properties: {
    isVersioningEnabled: true
    deleteRetentionPolicy: {
      enabled: true
      days: 30
    }
    containerDeleteRetentionPolicy: {
      enabled: true
      days: 30
    }
  }
}

resource historicalArchiveContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: historicalArchiveBlobService
  name: 'research-history'
  properties: {
    publicAccess: 'None'
  }
}

resource paperReportsContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: historicalArchiveBlobService
  name: 'paper-reports'
  properties: {
    publicAccess: 'None'
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
          name: 'DOTNET_ENVIRONMENT'
          value: 'Production'
        }
        {
          name: 'ConnectionStrings__TradingDb'
          value: tradingDbConnectionString
        }
        {
          name: 'MarketDataStreaming__Enabled'
          value: string(paperWorkerHostsEnabled)
        }
        {
          name: 'Experiments__PaperTraining__Enabled'
          value: string(paperWorkerHostsEnabled)
        }
        {
          name: 'Experiments__ProtectiveExits__Enabled'
          value: string(paperWorkerHostsEnabled)
        }
        {
          name: 'Entitlements__PaperWorkerLimitsEnabled'
          value: string(enforcePaperWorkerEntitlements)
        }
        {
          name: 'KeyVault__Uri'
          value: keyVault.properties.vaultUri
        }
        {
          name: 'Reporting__ContainerUri'
          value: '${historicalArchiveStorage.properties.primaryEndpoints.blob}${paperReportsContainer.name}'
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

resource appServicePaperReportsContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(paperReportsContainer.id, appService.id, blobDataContributorRoleDefinitionId)
  scope: paperReportsContainer
  properties: {
    principalId: appService.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: blobDataContributorRoleDefinitionId
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
          query: 'traces | where message has \'LiveOrderUnknown\' | summarize Count = count() by bin(timestamp, 5m)'
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
          query: 'traces | where message has \'LiveOrderRejected\' | summarize Count = count() by bin(timestamp, 5m)'
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

resource paperHostsMissingAlert 'Microsoft.Insights/scheduledQueryRules@2023-12-01' = {
  name: '${appName}-paper-hosts-missing'
  location: location
  properties: {
    displayName: '${appName}: paper host heartbeat missing'
    description: 'At least one paper host has not reported a successfully persisted SQL heartbeat in ten minutes. Check protection for open paper positions.'
    severity: 1
    enabled: paperWorkerHostsEnabled
    evaluationFrequency: 'PT5M'
    windowSize: 'PT10M'
    scopes: [
      applicationInsights.id
    ]
    criteria: {
      allOf: [
        {
          query: 'traces | where message contains "Paper market-data host heartbeat persisted." or message contains "Paper experiment host heartbeat persisted." | summarize MarketData = countif(message contains "Paper market-data host heartbeat persisted."), Experiments = countif(message contains "Paper experiment host heartbeat persisted.") | project Missing = iff(MarketData == 0 or Experiments == 0, 1, 0)'
          timeAggregation: 'Maximum'
          metricMeasureColumn: 'Missing'
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

resource paperForwardFeedMissingAlert 'Microsoft.Insights/scheduledQueryRules@2023-12-01' = {
  name: '${appName}-paper-feed-missing'
  location: location
  properties: {
    displayName: '${appName}: paper forward feed missing'
    description: 'No accepted closed one- or five-minute forward public candle was recorded in ten minutes. Investigate the market-data host and protective price freshness.'
    severity: 1
    enabled: paperWorkerHostsEnabled
    evaluationFrequency: 'PT5M'
    windowSize: 'PT10M'
    scopes: [
      applicationInsights.id
    ]
    criteria: {
      allOf: [
        {
          query: 'traces | where message contains "Paper forward closed candle persisted." | summarize Observed = count() | project Missing = iff(Observed == 0, 1, 0)'
          timeAggregation: 'Maximum'
          metricMeasureColumn: 'Missing'
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

resource paperProtectiveExitAlert 'Microsoft.Insights/scheduledQueryRules@2023-12-01' = {
  name: '${appName}-paper-protective-exit'
  location: location
  properties: {
    displayName: '${appName}: paper protective exit blocked'
    description: 'A paper protective-exit evaluation was blocked or an owner faulted. Inspect open positions and the closed one-minute market feed.'
    severity: 1
    enabled: paperWorkerHostsEnabled
    evaluationFrequency: 'PT5M'
    windowSize: 'PT5M'
    scopes: [
      applicationInsights.id
    ]
    criteria: {
      allOf: [
        {
          query: 'traces | where message contains "protective-exit owner was skipped for this tick" or message contains "Paper protective-exit evaluation returned" | summarize Count = count()'
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

resource paperMarketDataAlert 'Microsoft.Insights/scheduledQueryRules@2023-12-01' = {
  name: '${appName}-paper-market-data'
  location: location
  properties: {
    displayName: '${appName}: paper market-data disruption'
    description: 'The market-data worker loop or a paper scan failed safely. Inspect source continuity before allowing new paper admissions.'
    severity: 2
    enabled: paperWorkerHostsEnabled
    evaluationFrequency: 'PT5M'
    windowSize: 'PT5M'
    scopes: [
      applicationInsights.id
    ]
    criteria: {
      allOf: [
        {
          query: 'traces | where message contains "Market-data worker loop failed safely" or message contains "Continuous paper scan failed safely" | summarize Count = count()'
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

resource paperExperimentTickMissingAlert 'Microsoft.Insights/scheduledQueryRules@2023-12-01' = {
  name: '${appName}-paper-experiment-loop-missing'
  location: location
  properties: {
    displayName: '${appName}: paper experiment loop missing'
    description: 'The experiment worker has not completed a tick in ten minutes. A separate host heartbeat does not prove worker advancement.'
    severity: 1
    enabled: paperWorkerHostsEnabled
    evaluationFrequency: 'PT5M'
    windowSize: 'PT10M'
    scopes: [
      applicationInsights.id
    ]
    criteria: {
      allOf: [
        {
          query: 'traces | where message contains "Experiment tick completed." | summarize Ticks = count() | project Missing = iff(Ticks == 0, 1, 0)'
          timeAggregation: 'Maximum'
          metricMeasureColumn: 'Missing'
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

resource paperExperimentFaultAlert 'Microsoft.Insights/scheduledQueryRules@2023-12-01' = {
  name: '${appName}-paper-experiment-fault'
  location: location
  properties: {
    displayName: '${appName}: paper experiment worker fault'
    description: 'A worker or owner pool faulted during an experiment tick. Inspect isolated paper worker state without retrying an unknown order.'
    severity: 1
    enabled: paperWorkerHostsEnabled
    evaluationFrequency: 'PT5M'
    windowSize: 'PT5M'
    scopes: [
      applicationInsights.id
    ]
    criteria: {
      allOf: [
        {
          query: 'traces | where message contains "An experiment pool faulted" or (message contains "Experiment tick completed." and message !contains "Workers faulted: 0.") | summarize Faults = count()'
          timeAggregation: 'Count'
          metricMeasureColumn: 'Faults'
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

output appServiceManagedIdentityPrincipalId string = appService.identity.principalId
output keyVaultUri string = keyVault.properties.vaultUri
output applicationInsightsConnectionString string = applicationInsights.properties.ConnectionString
output historicalArchiveContainerUri string = '${historicalArchiveStorage.properties.primaryEndpoints.blob}${historicalArchiveContainer.name}'
output paperReportsContainerUri string = '${historicalArchiveStorage.properties.primaryEndpoints.blob}${paperReportsContainer.name}'
