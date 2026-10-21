param name string
param location string
param role 'frontend' | 'api' | 'controller' | 'agent'
param zone string
param vmSize string
param osImageResourceId string
param sshPublicKey string
param adminUsername string
param subnetResourceId string
param privateIpAddress string
param backendPoolResourceIds string[] = []
param securityGroupResourceId string
param registryName string
param registryLoginServer string
param image string
param privateDnsSuffix string
param configurationSecretUri string

var tags = { application: 'kardia', role: role, environment: 'production' }
var profile = {
  schemaVersion: 1
  role: role
  registryName: registryName
  registryLoginServer: registryLoginServer
  image: image
  privateDnsSuffix: privateDnsSuffix
  configurationSecretUri: configurationSecretUri
}
var cloudInit = {
  write_files: [
    {
      path: '/etc/kardia/role.json'
      owner: 'root:root'
      permissions: '0600'
      content: string(profile)
    }
    {
      path: '/usr/local/sbin/kardia-install-role'
      owner: 'root:root'
      permissions: '0700'
      encoding: 'b64'
      content: base64(loadTextContent('install-role.sh'))
    }
  ]
}

resource nic 'Microsoft.Network/networkInterfaces@2026-03-01' = {
  name: '${name}-nic'
  location: location
  tags: tags
  properties: {
    enableAcceleratedNetworking: true
    networkSecurityGroup: { id: securityGroupResourceId }
    ipConfigurations: [
      {
        name: 'private'
        properties: {
          primary: true
          privateIPAllocationMethod: 'Static'
          privateIPAddress: privateIpAddress
          subnet: { id: subnetResourceId }
          loadBalancerBackendAddressPools: [for id in backendPoolResourceIds: { id: id }]
        }
      }
    ]
  }
}

resource vm 'Microsoft.Compute/virtualMachines@2026-04-01' = {
  name: name
  location: location
  zones: [zone]
  tags: tags
  identity: { type: 'SystemAssigned' }
  properties: {
    hardwareProfile: { vmSize: vmSize }
    securityProfile: {
      securityType: 'TrustedLaunch'
      encryptionAtHost: true
      uefiSettings: { secureBootEnabled: true, vTpmEnabled: true }
    }
    storageProfile: {
      imageReference: { id: osImageResourceId }
      osDisk: {
        createOption: 'FromImage'
        diskSizeGB: 64
        managedDisk: { storageAccountType: 'Premium_LRS' }
        deleteOption: 'Detach'
      }
    }
    osProfile: {
      computerName: name
      adminUsername: adminUsername
      customData: base64('#cloud-config\n${string(cloudInit)}')
      linuxConfiguration: {
        disablePasswordAuthentication: true
        provisionVMAgent: true
        ssh: { publicKeys: [{ path: '/home/${adminUsername}/.ssh/authorized_keys', keyData: sshPublicKey }] }
        patchSettings: { patchMode: 'ImageDefault', assessmentMode: 'ImageDefault' }
      }
    }
    networkProfile: { networkInterfaces: [{ id: nic.id, properties: { primary: true, deleteOption: 'Detach' } }] }
    diagnosticsProfile: { bootDiagnostics: { enabled: true } }
  }
}

output principalId string = vm.identity.principalId
