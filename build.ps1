param([string]$FStudioBin = 'C:\Program Files (x86)\Flexem\FStudio 3.x\Bin')
$ErrorActionPreference = 'Stop'
$taskCompiler = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $taskCompiler)) { throw "Compiler missing: $taskCompiler" }
foreach ($taskName in @('Flexem.Studio.Core.dll', 'Flexem.Studio.Dtos.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $FStudioBin $taskName))) { throw "FStudio DLL missing: $taskName" }
}
New-Item -ItemType Directory -Path (Join-Path $PSScriptRoot 'artifacts') -Force | Out-Null
Push-Location $PSScriptRoot
try {
    & $taskCompiler /nologo /platform:x86 /out:artifacts\FStudioBridge.exe /r:System.Core.dll /r:System.Web.Extensions.dll src\Bridge.cs src\Catalog.cs
    if ($LASTEXITCODE -ne 0) { throw "Bridge build failed: $LASTEXITCODE" }
} finally { Pop-Location }
Write-Output "Built $PSScriptRoot\artifacts\FStudioBridge.exe"
