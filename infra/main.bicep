targetScope = 'subscription'

@minLength(1)
@maxLength(64)
@description('Name of the the environment which is used to generate a short unique hash used in all resources.')
param name string

@minLength(1)
@description('Location for all resources. This region must support Availability Zones.')
param location string

// If specified, will override the default format 'name-rg'
param resourceGroupName string = ''

// Use provided resource group name or generate one based on the environment name
var actualResourceGroupName = !empty(resourceGroupName) ? resourceGroupName : '${name}-rg'
var resourceToken = toLower(uniqueString(subscription().id, name, location))
var tags = { 'azd-env-name': name, 'created-by': 'azd' }

resource resourceGroup 'Microsoft.Resources/resourceGroups@2021-04-01' = {
  name: actualResourceGroupName
  location: location
  tags: tags
}

module resources 'resources.bicep' = {
  name: 'resources'
  scope: resourceGroup
  params: {
    name: name
    location: location
    resourceToken: resourceToken
    tags: tags
  }
}

output AZURE_LOCATION string = location
output AZURE_RESOURCE_GROUP string = resourceGroup.name
