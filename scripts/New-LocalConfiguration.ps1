$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
# Restrict before writing private files; inherit this ACL for every generated file.
New-Item -ItemType Directory -Path '.local' -Force | Out-Null
$taskIdentity = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
& icacls '.local' /inheritance:r /grant:r "${taskIdentity}:(OI)(CI)F" 'SYSTEM:(OI)(CI)F' | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Private directory ACL configuration failed.' }
dotnet run --project tools/Lab.Provisioner -- init
if ($LASTEXITCODE -ne 0) { throw 'Private configuration generation failed.' }
