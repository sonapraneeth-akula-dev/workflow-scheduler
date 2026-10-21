using 'main.bicep'

// Non-deployable examples: replace every resource ID, digest, version and address.
var subscription = '11111111-1111-4111-8111-111111111111'
var networkRoot = '/subscriptions/${subscription}/resourceGroups/REPLACE-network/providers/Microsoft.Network'
var subnet = '${networkRoot}/virtualNetworks/REPLACE-vnet/subnets/REPLACE-workloads'
var frontendPool = '${networkRoot}/loadBalancers/REPLACE-ingress/backendAddressPools/kardia-frontends'
var registry = 'REPLACEregistry'
var digest = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'

param namePrefix = 'kardia-prod'
param location = 'eastus2'
param osImageResourceId = '/subscriptions/${subscription}/resourceGroups/REPLACE-images/providers/Microsoft.Compute/galleries/REPLACE/images/kardia-linux/versions/1.0.0'
param sshPublicKey = 'REPLACE_WITH_APPROVED_SSH_PUBLIC_KEY'
param workloadCidr = '10.42.0.0/16'
param managementCidr = '10.43.0.0/24'
param ingressCidr = '10.44.0.0/24'
param privateDnsSuffix = 'kardia.internal'
param registryName = registry
param registryLoginServer = '${registry}.azurecr.io'
param configurationVaultName = 'REPLACE-kardia-config'
param storageAccountName = 'REPLACEkardiablobs'
param blobContainerName = 'kardia-blobs'
param configurationReferences = {
  frontend: { name: 'kardia-frontend', version: 'REPLACE_WITH_32_HEX_SECRET_VERSION' }
  api: { name: 'kardia-api', version: 'REPLACE_WITH_32_HEX_SECRET_VERSION' }
  controller: { name: 'kardia-controller', version: 'REPLACE_WITH_32_HEX_SECRET_VERSION' }
  agent: { name: 'kardia-agent', version: 'REPLACE_WITH_32_HEX_SECRET_VERSION' }
}
param images = {
  frontend: '${registry}.azurecr.io/kardia/frontend@sha256:${digest}'
  api: '${registry}.azurecr.io/kardia/control-plane@sha256:${digest}'
  controller: '${registry}.azurecr.io/kardia/control-plane@sha256:${digest}'
  agent: '${registry}.azurecr.io/kardia/agent@sha256:${digest}'
}
param frontendMachines = [
  { suffix: '01', subnetResourceId: subnet, privateIpAddress: '10.42.1.11', zone: '1', vmSize: 'Standard_D2s_v5', backendPoolResourceIds: [frontendPool] }
  { suffix: '02', subnetResourceId: subnet, privateIpAddress: '10.42.1.12', zone: '2', vmSize: 'Standard_D2s_v5', backendPoolResourceIds: [frontendPool] }
]
param apiMachines = [
  { suffix: '01', subnetResourceId: subnet, privateIpAddress: '10.42.1.21', zone: '1', vmSize: 'Standard_D2s_v5' }
  { suffix: '02', subnetResourceId: subnet, privateIpAddress: '10.42.1.22', zone: '2', vmSize: 'Standard_D2s_v5' }
]
param controllerMachines = [
  { suffix: '01', subnetResourceId: subnet, privateIpAddress: '10.42.1.31', zone: '1', vmSize: 'Standard_D2s_v5' }
  { suffix: '02', subnetResourceId: subnet, privateIpAddress: '10.42.1.32', zone: '2', vmSize: 'Standard_D2s_v5' }
]
param agentMachines = [
  { suffix: '01', subnetResourceId: subnet, privateIpAddress: '10.42.1.41', zone: '1', vmSize: 'Standard_D4s_v5' }
  { suffix: '02', subnetResourceId: subnet, privateIpAddress: '10.42.1.42', zone: '2', vmSize: 'Standard_D4s_v5' }
]
