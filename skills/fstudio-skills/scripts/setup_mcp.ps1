<#
.SYNOPSIS
构建下载包内置的 MCP，并生成匹配绝对路径的客户端配置片段。
.DESCRIPTION
仅写入包内 mcp/artifacts、mcp/mcp.local.json 及指定工作区目录；不改全局客户端配置、
不启动 FStudio、不下载到触摸屏，也不包含或复制厂商 DLL。应先把技能包放在最终位置再运行。
.PARAMETER PythonExecutable
Python 3.11 或更高版本解释器的完整路径。
.PARAMETER FStudioRoot
本机已合法安装的 FStudio 3 根目录。
.PARAMETER Workspace
MCP 可操作的工程工作区；默认使用包内 mcp/projects，建议为工程选择较短路径。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$PythonExecutable,
    [string]$FStudioRoot='C:\Program Files (x86)\Flexem\FStudio 3.x',
    [string]$Workspace
)
$ErrorActionPreference='Stop'
if ($PSVersionTable.PSVersion.Major -lt 7 -or -not [Environment]::Is64BitProcess) {
    throw '请使用 64 位 PowerShell 7 运行此脚本。'
}
$taskSkillRoot=(Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$taskMcpRoot=Join-Path $taskSkillRoot 'mcp'
if (-not (Test-Path -LiteralPath (Join-Path $taskMcpRoot 'server.py') -PathType Leaf)) {
    throw '当前目录没有内置 MCP；请使用 Release 的完整 Fstudio_skills.zip，或继续使用已配置的独立 MCP。'
}
if (-not [IO.Path]::IsPathFullyQualified($PythonExecutable) -or -not (Test-Path -LiteralPath $PythonExecutable -PathType Leaf)) {
    throw '请传入现有 Python 解释器的完整路径。'
}
& $PythonExecutable -c 'import sys; raise SystemExit(0 if sys.version_info >= (3, 11) else 1)'
if ($LASTEXITCODE -ne 0) { throw '需要 Python 3.11 或更高版本。' }
$taskFStudioRoot=(Resolve-Path -LiteralPath $FStudioRoot).Path
$taskBin=Join-Path $taskFStudioRoot 'Bin'
if (-not $Workspace) { $Workspace=Join-Path $taskMcpRoot 'projects' }
$taskWorkspace=[IO.Path]::GetFullPath($Workspace)
& (Join-Path $taskMcpRoot 'build.ps1') -FStudioBin $taskBin
& (Join-Path $taskMcpRoot 'install-visible-host.ps1') -FStudioRoot $taskFStudioRoot
New-Item -ItemType Directory -Path $taskWorkspace -Force | Out-Null
# 客户端与原生宿主必须使用同一工作区，避免连接成功但项目被范围保护拒绝。
@{workspace=$taskWorkspace;state_directory=(Join-Path $taskMcpRoot 'artifacts\visible-host')} |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $taskMcpRoot 'artifacts\visible-addin\host-settings.json') -Encoding utf8
$taskConfig=Join-Path $taskMcpRoot 'mcp.local.json'
@{mcpServers=@{fstudio=@{command=$PythonExecutable;args=@((Join-Path $taskMcpRoot 'server.py'),'--workspace',$taskWorkspace,'--fstudio-bin',$taskBin)}}} |
    ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $taskConfig -Encoding utf8
Write-Output "已构建内置 MCP。客户端配置片段：$taskConfig"
Write-Output '将此 stdio 配置接入你的 AI 客户端；若已有同名连接，应更新原连接，避免重复注册。'
