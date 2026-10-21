targetScope = 'resourceGroup'

type machine = {
  suffix: string
  subnetResourceId: string
  privateIpAddress: string
  zone: '1' | '2' | '3'
  vmSize: string
  backendPoolResourceIds: string[]?
}

@description('Existing approved Linux image version with Docker, Azure CLI, jq, curl and cloud-init installed. No passwords or application secrets in the image.')
param osImageResourceId string
@minLength(1)
param sshPublicKey string
param adminUsername string = 'kardia'
@maxLength(24)
param namePrefix string = 'kardia-prod'
param location string = resourceGroup().location
@description('Approved private workload CIDR. Existing subnets must have private DNS, explicit outbound connectivity and access to managed data services.')
param workloadCidr string
@description('Bastion/VPN management CIDR; never Internet or wildcard.')
param managementCidr string
@description('Private ingress/load-balancer client CIDR permitted to reach frontend TLS.')
param ingressCidr string
@description('Existing private DNS zone; create records for the static VM addresses before installing roles.')
param privateDnsSuffix string
@minLength(2)
param frontendMachines machine[]
@minLength(2)
param apiMachines machine[]
@minLength(2)
param controllerMachines machine[]
@minLength(2)
param agentMachines machine[]
@description('Existing RBAC-mode registry in this resource group. Admin and anonymous access must be disabled.')
param registryName string
param registryLoginServer string
@description('Immutable container references, each including @sha256:<64 lowercase hex digits>.')
param images {
  frontend: string
  api: string
  controller: string
  agent: string
}
@description('Existing RBAC Key Vault in this resource group, with purge protection and private networking. Only secret URIs enter VM metadata.')
param configurationVaultName string
param configurationReferences {
  frontend: { name: string, version: string }
  api: { name: string, version: string }
  controller: { name: string, version: string }
  agent: { name: string, version: string }
}
@description('Existing private, key-disabled storage account/container in this resource group.')
param storageAccountName string
param blobContainerName string = 'kardia-blobs'

var roles = ['frontend', 'api', 'controller', 'agent']
var machines = concat(
  map(frontendMachines, node => union(node, { role: 'frontend' })),
  map(apiMachines, node => union(node, { role: 'api' })),
  map(controllerMachines, node => union(node, { role: 'controller' })),
  map(agentMachines, node => union(node, { role: 'agent' }))
)
var azureHttpsTags = ['AzureActiveDirectory', 'AzureResourceManager', 'AzureContainerRegistry', 'Storage', 'AzureKeyVault', 'AzureMonitor']
var azureHttpsRules = [for (tag, index) in azureHttpsTags: {
  name: 'https-${tag}'
  properties: {
    priority: 200 + index, direction: 'Outbound', access: 'Allow', protocol: 'Tcp'
    sourceAddressPrefix: '*', sourcePortRange: '*'
    destinationAddressPrefix: tag, destinationPortRange: '443'
  }
}]

resource registry 'Microsoft.ContainerRegistry/registries@2026-09-01-preview' existing = {
  name: registryName
}
resource vault 'Microsoft.KeyVault/vaults@2026-06-01-preview' existing = {
  name: configurationVaultName
}
resource secrets 'Microsoft.KeyVault/vaults/secrets@2026-05-15' existing = [for role in roles: {
  parent: vault
  name: configurationReferences[role].name
}]
resource storage 'Microsoft.Storage/storageAccounts@2026-09-01' existing = {
  name: storageAccountName
}
resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2026-09-01' existing = {
  parent: storage
  name: 'default'
}
resource container 'Microsoft.Storage/storageAccounts/blobServices/containers@2026-09-01' existing = {
  parent: blobService
  name: blobContainerName
}

resource security 'Microsoft.Network/networkSecurityGroups@2026-03-01' = [for role in roles: {
  name: '${namePrefix}-${role}-nsg'
  location: location
  tags: { application: 'kardia', role: role, environment: 'production' }
  properties: {
    securityRules: concat([
      {
        name: 'private-management'
        properties: {
          priority: 100, direction: 'Inbound', access: 'Allow', protocol: 'Tcp'
          sourceAddressPrefix: managementCidr, sourcePortRange: '*'
          destinationAddressPrefix: '*', destinationPortRange: '22'
        }
      }
    ], role == 'agent' ? [] : [
      {
        name: 'private-role-tls'
        properties: {
          priority: 200, direction: 'Inbound', access: 'Allow', protocol: 'Tcp'
          sourceAddressPrefix: role == 'frontend' ? ingressCidr : workloadCidr, sourcePortRange: '*'
          destinationAddressPrefix: '*', destinationPortRange: role == 'controller' ? '8444' : '8443'
        }
      }
      {
        name: 'load-balancer-health'
        properties: {
          priority: 210, direction: 'Inbound', access: 'Allow', protocol: 'Tcp'
          sourceAddressPrefix: 'AzureLoadBalancer', sourcePortRange: '*'
          destinationAddressPrefix: '*', destinationPortRange: role == 'controller' ? '8444' : '8443'
        }
      }
    ], [
      {
        name: 'deny-other-ingress'
        properties: {
          priority: 4096, direction: 'Inbound', access: 'Deny', protocol: '*'
          sourceAddressPrefix: '*', sourcePortRange: '*'
          destinationAddressPrefix: '*', destinationPortRange: '*'
        }
      }
      {
        name: 'private-data-and-peers'
        properties: {
          priority: 100, direction: 'Outbound', access: 'Allow', protocol: '*'
          sourceAddressPrefix: '*', sourcePortRange: '*'
          destinationAddressPrefix: 'VirtualNetwork', destinationPortRange: '*'
        }
      }
      {
        name: 'azure-dns'
        properties: {
          priority: 110, direction: 'Outbound', access: 'Allow', protocol: '*'
          sourceAddressPrefix: '*', sourcePortRange: '*'
          destinationAddressPrefix: 'AzurePlatformDNS', destinationPortRange: '53'
        }
      }
      {
        name: 'managed-identity'
        properties: {
          priority: 120, direction: 'Outbound', access: 'Allow', protocol: 'Tcp'
          sourceAddressPrefix: '*', sourcePortRange: '*'
          destinationAddressPrefix: 'AzurePlatformIMDS', destinationPortRange: '80'
        }
      }
      {
        name: 'deny-other-egress'
        properties: {
          priority: 4096, direction: 'Outbound', access: 'Deny', protocol: '*'
          sourceAddressPrefix: '*', sourcePortRange: '*'
          destinationAddressPrefix: '*', destinationPortRange: '*'
        }
      }
    ], azureHttpsRules)
  }
}]

module hosts 'vm.bicep' = [for node in machines: {
  name: '${namePrefix}-${node.role}-${node.suffix}'
  params: {
    name: '${namePrefix}-${node.role}-${node.suffix}'
    location: location
    role: node.role
    zone: node.zone
    vmSize: node.vmSize
    osImageResourceId: osImageResourceId
    sshPublicKey: sshPublicKey
    adminUsername: adminUsername
    subnetResourceId: node.subnetResourceId
    privateIpAddress: node.privateIpAddress
    backendPoolResourceIds: node.?backendPoolResourceIds ?? []
    securityGroupResourceId: security[indexOf(roles, node.role)].id
    registryName: registryName
    registryLoginServer: registryLoginServer
    image: images[node.role]
    privateDnsSuffix: privateDnsSuffix
    configurationSecretUri: 'https://${configurationVaultName}${environment().suffixes.keyvaultDns}/secrets/${configurationReferences[node.role].name}/${configurationReferences[node.role].version}'
  }
}]

resource pull 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for (node, index) in machines: {
  name: guid(registry.id, '${namePrefix}-${node.role}-${node.suffix}', 'kardia-pull')
  scope: registry
  properties: {
    principalId: hosts[index].outputs.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
  }
}]
resource configurationRead 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for (node, index) in machines: {
  name: guid(secrets[indexOf(roles, node.role)].id, '${namePrefix}-${node.role}-${node.suffix}', 'kardia-config')
  scope: secrets[indexOf(roles, node.role)]
  properties: {
    principalId: hosts[index].outputs.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')
  }
}]
resource blobs 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for (node, index) in machines: if (contains(['api', 'controller'], node.role)) {
  name: guid(container.id, '${namePrefix}-${node.role}-${node.suffix}', 'kardia-blobs')
  scope: container
  properties: {
    principalId: hosts[index].outputs.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
  }
}]

output instances array = [for (node, index) in machines: {
  name: '${namePrefix}-${node.role}-${node.suffix}'
  role: node.role
  privateIpAddress: node.privateIpAddress
  principalId: hosts[index].outputs.principalId
}]
