// ═══════════════════════════════════════════════════════════
// MAIN INFRASTRUCTURE TEMPLATE
// Deploys complete production-ready Order & Catalog Service
// ═══════════════════════════════════════════════════════════

targetScope = 'subscription'

@description('Primary Azure region')
param location string = 'eastus'

@description('Secondary region for disaster recovery')
param secondaryLocation string = 'westus2'

@description('Environment name')
@allowed([
  'dev'
  'staging'
  'prod'
])
param environment string = 'prod'

@description('Application name')
param appName string = 'order-catalog'

@description('Unique suffix for global resources')
param uniqueSuffix string = uniqueString(subscription().subscriptionId, appName)

// ═══════════════════════════════════════════════════════════
// RESOURCE GROUP
// ═══════════════════════════════════════════════════════════

resource rg 'Microsoft.Resources/resourceGroups@2021-04-01' = {
  name: '${appName}-${environment}-rg'
  location: location
  tags: {
    Environment: environment
    Application: appName
    ManagedBy: 'Bicep'
    CostCenter: 'Engineering'
  }
}

// ═══════════════════════════════════════════════════════════
// KEY VAULT (Secrets Management)
// ═══════════════════════════════════════════════════════════

module keyVault 'modules/keyvault.bicep' = {
  scope: rg
  name: 'keyVaultDeployment'
  params: {
    name: '${appName}-${environment}-kv-${uniqueSuffix}'
    location: location
    enablePurgeProtection: environment == 'prod'
    enableSoftDelete: true
    softDeleteRetentionDays: 90
    enableRbacAuthorization: true
    networkAcls: {
      defaultAction: 'Deny'
      bypass: 'AzureServices'
      ipRules: []
    }
  }
}

// ═══════════════════════════════════════════════════════════
// POSTGRESQL HYPERSCALE (Citus) - Primary Database
// ═══════════════════════════════════════════════════════════

module postgresql 'modules/postgresql.bicep' = {
  scope: rg
  name: 'postgresqlDeployment'
  params: {
    serverName: '${appName}-${environment}-pg-${uniqueSuffix}'
    location: location
    administratorLogin: 'pgadmin'
    administratorLoginPassword: 'P@ssw0rd123!' // TODO: Use Key Vault reference
    skuName: environment == 'prod' ? 'GP_Gen5_8' : 'GP_Gen5_4'
    storageSizeGB: environment == 'prod' ? 512 : 128
    backupRetentionDays: environment == 'prod' ? 35 : 7
    geoRedundantBackup: environment == 'prod' ? 'Enabled' : 'Disabled'
    enableHighAvailability: environment == 'prod'
    enableAutoGrow: true
    databaseName: 'OrderCatalogDb'
    // Connection pooling settings
    maxConnections: 200
    // Performance tuning
    sharedBuffers: 2048 // MB
    effectiveCacheSize: 6144 // MB
    maintenanceWorkMem: 512 // MB
    workMem: 10485 // KB (10MB)
  }
}

// ═══════════════════════════════════════════════════════════
// AZURE CACHE FOR REDIS (Premium Cluster)
// ═══════════════════════════════════════════════════════════

module redis 'modules/redis.bicep' = {
  scope: rg
  name: 'redisDeployment'
  params: {
    name: '${appName}-${environment}-redis-${uniqueSuffix}'
    location: location
    sku: environment == 'prod' ? 'Premium' : 'Standard'
    skuFamily: environment == 'prod' ? 'P' : 'C'
    capacity: environment == 'prod' ? 4 : 1 // P4 = 26GB, C1 = 1GB
    enableNonSslPort: false
    minimumTlsVersion: '1.2'
    // Premium features
    shardCount: environment == 'prod' ? 3 : 0
    replicasPerMaster: environment == 'prod' ? 2 : 0
    // Persistence (Premium only)
    rdbBackupEnabled: environment == 'prod'
    rdbBackupFrequency: 60 // minutes
    rdbBackupMaxSnapshotCount: 1
    // Clustering
    enableClustering: environment == 'prod'
    // Configuration
    maxmemoryPolicy: 'allkeys-lru'
    maxmemoryReserved: 50
    maxfragmentationmemoryReserved: 50
  }
}

// ═══════════════════════════════════════════════════════════
// AZURE SERVICE BUS (Event Publishing)
// ═══════════════════════════════════════════════════════════

module serviceBus 'modules/servicebus.bicep' = {
  scope: rg
  name: 'serviceBusDeployment'
  params: {
    namespaceName: '${appName}-${environment}-sb-${uniqueSuffix}'
    location: location
    sku: environment == 'prod' ? 'Premium' : 'Standard'
    capacity: environment == 'prod' ? 2 : 1 // Messaging units
    zoneRedundant: environment == 'prod'
    topics: [
      {
        name: 'order-events'
        maxSizeInMegabytes: 5120
        requiresDuplicateDetection: true
        defaultMessageTimeToLive: 'P7D' // 7 days
        enableBatchedOperations: true
        enablePartitioning: environment == 'prod'
        subscriptions: [
          {
            name: 'analytics-subscription'
            requiresSession: false
            deadLetteringOnMessageExpiration: true
            maxDeliveryCount: 10
          }
          {
            name: 'notification-subscription'
            requiresSession: false
            deadLetteringOnMessageExpiration: true
            maxDeliveryCount: 10
          }
        ]
      }
      {
        name: 'webhook-events'
        maxSizeInMegabytes: 1024
        requiresDuplicateDetection: true
        enableBatchedOperations: true
      }
    ]
  }
}

// ═══════════════════════════════════════════════════════════
// AZURE KUBERNETES SERVICE (AKS)
// ═══════════════════════════════════════════════════════════

module aks 'modules/aks.bicep' = {
  scope: rg
  name: 'aksDeployment'
  params: {
    clusterName: '${appName}-${environment}-aks'
    location: location
    dnsPrefix: '${appName}-${environment}'
    kubernetesVersion: '1.28.3'
    enableRBAC: true
    enablePodSecurityPolicy: false
    enableAzurePolicy: true
    // Node pools
    systemNodePool: {
      name: 'system'
      vmSize: 'Standard_D4s_v3'
      minCount: 3
      maxCount: 5
      enableAutoScaling: true
      osDiskSizeGB: 128
      osType: 'Linux'
      mode: 'System'
      availabilityZones: environment == 'prod' ? ['1', '2', '3'] : []
    }
    userNodePool: {
      name: 'apps'
      vmSize: 'Standard_D8s_v3' // 8 vCPU, 32GB RAM
      minCount: environment == 'prod' ? 10 : 3
      maxCount: environment == 'prod' ? 100 : 10
      enableAutoScaling: true
      osDiskSizeGB: 256
      osType: 'Linux'
      mode: 'User'
      availabilityZones: environment == 'prod' ? ['1', '2', '3'] : []
      nodeLabels: {
        workload: 'order-service'
      }
      nodeTaints: []
    }
    // Networking
    networkProfile: {
      networkPlugin: 'azure'
      networkPolicy: 'azure'
      loadBalancerSku: 'Standard'
      serviceCidr: '10.0.0.0/16'
      dnsServiceIP: '10.0.0.10'
      dockerBridgeCidr: '172.17.0.1/16'
    }
    // Monitoring
    enableMonitoring: true
    enableContainerInsights: true
    // Security
    enablePrivateCluster: environment == 'prod'
    enableSecretStoreCSIDriver: true
    enableWorkloadIdentity: true
  }
}

// ═══════════════════════════════════════════════════════════
// AZURE API MANAGEMENT (APIM)
// ═══════════════════════════════════════════════════════════

module apim 'modules/apim.bicep' = {
  scope: rg
  name: 'apimDeployment'
  params: {
    name: '${appName}-${environment}-apim'
    location: location
    publisherEmail: 'api@company.com'
    publisherName: 'Company Name'
    sku: environment == 'prod' ? 'Premium' : 'Developer'
    skuCount: environment == 'prod' ? 2 : 1
    virtualNetworkType: environment == 'prod' ? 'Internal' : 'None'
    enableClientCertificate: environment == 'prod'
    // Backend service
    backendServiceUrl: 'https://${aks.outputs.ingressIp}/api'
    // Global policies
    enableRateLimiting: true
    requestsPerMinute: environment == 'prod' ? 10000 : 1000
    // Security
    enableManagedIdentity: true
    // Monitoring
    enableApplicationInsights: true
  }
}

// ═══════════════════════════════════════════════════════════
// AZURE APPLICATION INSIGHTS (Monitoring)
// ═══════════════════════════════════════════════════════════

module appInsights 'modules/appinsights.bicep' = {
  scope: rg
  name: 'appInsightsDeployment'
  params: {
    name: '${appName}-${environment}-ai'
    location: location
    applicationType: 'web'
    retentionInDays: environment == 'prod' ? 90 : 30
    disableIpMasking: false
    enableIngestion: true
    enableQuery: true
    // Sampling
    samplingPercentage: environment == 'prod' ? 10 : 100
    // Alerts
    enableSmartDetection: true
  }
}

// ═══════════════════════════════════════════════════════════
// LOG ANALYTICS WORKSPACE
// ═══════════════════════════════════════════════════════════

module logAnalytics 'modules/loganalytics.bicep' = {
  scope: rg
  name: 'logAnalyticsDeployment'
  params: {
    name: '${appName}-${environment}-logs'
    location: location
    sku: 'PerGB2018'
    retentionInDays: environment == 'prod' ? 90 : 30
    dailyQuotaGb: environment == 'prod' ? 10 : 1
  }
}

// ═══════════════════════════════════════════════════════════
// AZURE CONTAINER REGISTRY (ACR)
// ═══════════════════════════════════════════════════════════

module acr 'modules/acr.bicep' = {
  scope: rg
  name: 'acrDeployment'
  params: {
    name: '${appName}${environment}acr${uniqueSuffix}'
    location: location
    sku: environment == 'prod' ? 'Premium' : 'Standard'
    adminUserEnabled: false
    enableZoneRedundancy: environment == 'prod'
    enableContentTrust: environment == 'prod'
    enablePurgeRetention: environment == 'prod'
    retentionDays: 30
    // Networking
    enablePublicNetworkAccess: environment != 'prod'
    enablePrivateEndpoint: environment == 'prod'
  }
}

// ═══════════════════════════════════════════════════════════
// AZURE APP CONFIGURATION (Feature Flags & Config)
// ═══════════════════════════════════════════════════════════

module appConfig 'modules/appconfig.bicep' = {
  scope: rg
  name: 'appConfigDeployment'
  params: {
    name: '${appName}-${environment}-config-${uniqueSuffix}'
    location: location
    sku: environment == 'prod' ? 'Standard' : 'Free'
    enablePurgeProtection: environment == 'prod'
    softDeleteRetentionDays: 7
  }
}

// ═══════════════════════════════════════════════════════════
// AZURE FRONT DOOR (Global Load Balancer + WAF)
// ═══════════════════════════════════════════════════════════

module frontDoor 'modules/frontdoor.bicep' = if (environment == 'prod') {
  scope: rg
  name: 'frontDoorDeployment'
  params: {
    name: '${appName}-${environment}-fd'
    enableWaf: true
    wafMode: 'Prevention'
    backends: [
      {
        address: apim.outputs.gatewayUrl
        priority: 1
        weight: 100
        enabledState: 'Enabled'
      }
    ]
    // Health probe
    healthProbeIntervalSeconds: 30
    healthProbePath: '/health/ready'
    healthProbeProtocol: 'Https'
    // Routing rules
    enableHttpsRedirect: true
    forwardingProtocol: 'HttpsOnly'
  }
}

// ═══════════════════════════════════════════════════════════
// OUTPUTS
// ═══════════════════════════════════════════════════════════

output resourceGroupName string = rg.name
output keyVaultName string = keyVault.outputs.name
output postgresqlServerName string = postgresql.outputs.serverName
output postgresqlConnectionString string = postgresql.outputs.connectionString
output redisHostName string = redis.outputs.hostName
output redisPrimaryKey string = redis.outputs.primaryKey
output serviceBusNamespace string = serviceBus.outputs.namespaceName
output serviceBusConnectionString string = serviceBus.outputs.connectionString
output aksClusterName string = aks.outputs.clusterName
output aksIngressIp string = aks.outputs.ingressIp
output apimGatewayUrl string = apim.outputs.gatewayUrl
output appInsightsInstrumentationKey string = appInsights.outputs.instrumentationKey
output acrLoginServer string = acr.outputs.loginServer
output appConfigEndpoint string = appConfig.outputs.endpoint
output frontDoorEndpoint string = environment == 'prod' ? frontDoor.outputs.endpoint : ''