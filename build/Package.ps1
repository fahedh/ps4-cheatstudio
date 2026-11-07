param([string]$Artifacts = (Join-Path $PSScriptRoot '..' 'artifacts'))
$ErrorActionPreference = 'Stop'
python (Join-Path $PSScriptRoot 'package.py') --artifacts $Artifacts
if ($LASTEXITCODE -ne 0) { throw 'Portable package generation/validation failed' }
