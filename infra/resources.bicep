@description('Name of the environment which is used to generate a short unique hash used in all resources.')
param name string

@description('The location where all resources will be deployed.')
param location string

@description('A unique token generated for the deployment.')
param resourceToken string

@description('Tags that will be applied to all resources.')
param tags object

var appServicePlanName = 'plan-${resourceToken}'
var appServiceName = 'app-${resourceToken}'

resource appServicePlan 'Microsoft.Web/serverfarms@2022-03-01' = {
  name: appServicePlanName
  location: location
  tags: tags
  sku: {
    name: 'B1'
  }
  properties: {}
}

resource appService 'Microsoft.Web/sites@2022-03-01' = {
  name: appServiceName
  location: location
  kind: 'app'
  tags: union(tags, { 'azd-service-name': 'web' })
  properties: {
    serverFarmId: appServicePlan.id
    httpsOnly: true
    clientAffinityEnabled: true
    siteConfig: {
      minTlsVersion: '1.2'
      http20Enabled: true
      alwaysOn: true
      netFrameworkVersion: 'v6.0'
      ftpsState: 'Disabled'
    }
  }
  
  resource appSettings 'config' = {
    name: 'appsettings'
    properties: {
      SCM_DO_BUILD_DURING_DEPLOYMENT: 'true'
      WEBSITE_HTTPLOGGING_RETENTION_DAYS: '3'
      WEBSITE_RUN_FROM_PACKAGE: '1'
      ASPNETCORE_ENVIRONMENT: 'Production'
      DOTNET_RUNNING_IN_CONTAINER: 'false'
    }
  }
}

// Output the website URL
output AZURE_APP_SERVICE_NAME string = appService.name
output WEBSITE_URL string = 'https://${appService.properties.defaultHostName}'
