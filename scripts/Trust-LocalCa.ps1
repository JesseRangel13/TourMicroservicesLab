$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
$taskCertificatePath = (Resolve-Path '.local/certificates/ca.crt').Path
Import-Certificate -FilePath $taskCertificatePath -CertStoreLocation 'Cert:\CurrentUser\Root' | Select-Object Subject,Thumbprint
Write-Host 'Trusted this lab CA for the current Windows user. Private CA key stays local.'
