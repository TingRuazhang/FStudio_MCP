param([string]$HostDll = (Join-Path $PSScriptRoot '..\artifacts\FStudioMcp.Visible.dll'))
$ErrorActionPreference = 'Stop'
$taskFramework = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319'
$taskOutput = Join-Path $PSScriptRoot '..\artifacts\drawing-contract-tests.exe'
$taskSource = Join-Path $PSScriptRoot 'DrawingContractsHarness.cs'
# 测试只加载宿主类型与内存模型；不打开、关闭或修改任何用户工程。
& "$taskFramework\csc.exe" /nologo /platform:x86 /r:System.Core.dll "/out:$taskOutput" $taskSource
if ($LASTEXITCODE -ne 0) { throw 'Drawing contracts harness build failed' }
& $taskOutput ([IO.Path]::GetFullPath($HostDll)) (Join-Path $PSScriptRoot '..\projects')
if ($LASTEXITCODE -ne 0) { throw 'Drawing contracts harness failed' }
# 同一测试入口追加真实分发器的编译所有权验证；仅构造内存记录，不启动宿主或模拟进程。
$taskGuardOutput = Join-Path $PSScriptRoot '..\artifacts\preview-build-guard-tests.exe'
$taskGuardSource = Join-Path $PSScriptRoot 'PreviewBuildGuardHarness.cs'
& "$taskFramework\csc.exe" /nologo /platform:x86 /r:System.Core.dll "/out:$taskGuardOutput" $taskGuardSource
if ($LASTEXITCODE -ne 0) { throw 'Preview build guard harness build failed' }
& $taskGuardOutput ([IO.Path]::GetFullPath($HostDll))
if ($LASTEXITCODE -ne 0) { throw 'Preview build guard harness failed' }
