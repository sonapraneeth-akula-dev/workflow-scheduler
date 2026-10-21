function New-LabServerCertificate(
    [Security.Cryptography.X509Certificates.X509Certificate2]$Issuer,
    [string]$CertificatePath, [string]$KeyPath, [string[]]$DnsNames, [Net.IPAddress[]]$Addresses
) {
    $key = [Security.Cryptography.RSA]::Create(3072)
    try {
        $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new(
            "CN=$($DnsNames[0])", $key, [Security.Cryptography.HashAlgorithmName]::SHA256,
            [Security.Cryptography.RSASignaturePadding]::Pkcs1)
        $san = [Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder]::new()
        foreach ($name in $DnsNames) { $san.AddDnsName($name) }
        foreach ($address in $Addresses) { $san.AddIpAddress($address) }
        $request.CertificateExtensions.Add($san.Build())
        $request.CertificateExtensions.Add(
            [Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($false, $false, 0, $true))
        $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
            [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature -bor
            [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::KeyEncipherment, $true))
        $eku = [Security.Cryptography.OidCollection]::new()
        $null = $eku.Add([Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.1'))
        $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($eku, $false))
        $request.CertificateExtensions.Add(
            [Security.Cryptography.X509Certificates.X509SubjectKeyIdentifierExtension]::new($request.PublicKey, $false))
        $request.CertificateExtensions.Add(
            [Security.Cryptography.X509Certificates.X509AuthorityKeyIdentifierExtension]::CreateFromCertificate($Issuer, $true, $false))
        $server = $request.Create($Issuer, [datetimeoffset]::UtcNow.AddMinutes(-5),
            [datetimeoffset]::UtcNow.AddDays(30), [Security.Cryptography.RandomNumberGenerator]::GetBytes(16))
        try {
            [IO.File]::WriteAllText($CertificatePath, $server.ExportCertificatePem())
            [IO.File]::WriteAllText($KeyPath, $key.ExportPkcs8PrivateKeyPem())
        } finally { $server.Dispose() }
    } finally { $key.Dispose() }
}
