<#
.SYNOPSIS
    预览或清理 Banori 的构建、测试和 E2E 生成产物。

.DESCRIPTION
    默认只显示清理计划。只有显式传入 -Apply 才会删除；-WhatIf 和 -Confirm 也受支持。
    不触碰应用数据、测试源码、E2E 工具或 .e2e/smoke/qb-notes.md。

.PARAMETER Scope
    All 清理全部白名单产物；Build 只清理 bin/obj/TestResults；E2E 只清理 E2E 运行产物。

.PARAMETER Apply
    执行清理。省略时仅预览。

.EXAMPLE
    .\scripts\clean-test-artifacts.ps1
    预览全部清理目标。

.EXAMPLE
    .\scripts\clean-test-artifacts.ps1 -Scope E2E -Apply
    确认后清理 E2E 运行产物。
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [ValidateSet('All', 'Build', 'E2E')]
    [string]$Scope = 'All',

    # 预览是默认行为；只有显式传入 -Apply 才会请求删除。
    [switch]$Apply
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:RepositoryRoot = [System.IO.Path]::GetFullPath(
    (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path)
$script:RepositoryRootPrefix = $script:RepositoryRoot.TrimEnd([char[]]@('\', '/')) +
                               [System.IO.Path]::DirectorySeparatorChar
$script:Actions = [System.Collections.Generic.List[object]]::new()

function Assert-NotReparsePoint {
    param(
        [Parameter(Mandatory)]
        [System.IO.FileSystemInfo]$Item
    )

    if (($Item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "拒绝清理包含链接/联接点的路径：$($Item.FullName)"
    }
}

function Get-CheckedRepositoryPath {
    param(
        [Parameter(Mandatory)]
        [string]$RelativePath
    )

    if ([System.IO.Path]::IsPathRooted($RelativePath) -or
        $RelativePath -match '(^|[\\/])\.\.([\\/]|$)') {
        throw "清理目标必须是仓库内的固定相对路径：$RelativePath"
    }

    $fullPath = [System.IO.Path]::GetFullPath(
        (Join-Path $script:RepositoryRoot $RelativePath))
    if (-not $fullPath.StartsWith(
            $script:RepositoryRootPrefix,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "拒绝清理仓库目录之外的路径：$fullPath"
    }

    # 检查路径的每一层，避免仓库内的 junction/symlink 把目标引到仓库外。
    $currentPath = $script:RepositoryRoot
    foreach ($segment in ($RelativePath -split '[\\/]')) {
        if ([string]::IsNullOrWhiteSpace($segment)) {
            continue
        }

        $currentPath = Join-Path $currentPath $segment
        if (-not (Test-Path -LiteralPath $currentPath)) {
            break
        }

        Assert-NotReparsePoint (Get-Item -LiteralPath $currentPath -Force)
    }

    return $fullPath
}

function Assert-TreeHasNoReparsePoints {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $rootItem = Get-Item -LiteralPath $Path -Force
    Assert-NotReparsePoint $rootItem

    if ($rootItem.PSIsContainer) {
        Get-ChildItem -LiteralPath $Path -Force -Recurse -ErrorAction Stop |
            ForEach-Object { Assert-NotReparsePoint $_ }
    }
}

function Add-CleanupAction {
    param(
        [Parameter(Mandatory)]
        [string]$RelativePath,

        [Parameter(Mandatory)]
        [string]$Description
    )

    $fullPath = Get-CheckedRepositoryPath $RelativePath
    if (-not (Test-Path -LiteralPath $fullPath)) {
        return
    }

    Assert-TreeHasNoReparsePoints $fullPath
    $script:Actions.Add([pscustomobject]@{
            Path        = $fullPath
            Description = $Description
        })
}

if ($Scope -in @('All', 'Build')) {
    # 覆盖解决方案项目及独立 E2E 项目的常规、NativeAOT 编译/发布输出。
    $projectDirectories = @(
        '.',
        'Banori',
        'Banori.Core',
        'Banori.Tests',
        '.e2e/Banori.E2E.Tests'
    )

    foreach ($projectDirectory in $projectDirectories) {
        foreach ($artifactDirectory in @('bin', 'obj', 'TestResults')) {
            $relativePath = if ($projectDirectory -eq '.') {
                $artifactDirectory
            }
            else {
                Join-Path $projectDirectory $artifactDirectory
            }

            Add-CleanupAction $relativePath "构建/测试输出 $relativePath"
        }
    }
}

if ($Scope -in @('All', 'E2E')) {
    foreach ($relativePath in @('.e2e/s1', '.e2e/s2', '.e2e/smoke-tmp')) {
        Add-CleanupAction $relativePath "E2E 运行产物 $relativePath"
    }

    # smoke/qb-notes.md 是人工实测记录；只清除此目录中的其他运行产物。
    $smokeRelativePath = '.e2e/smoke'
    $smokePath = Get-CheckedRepositoryPath $smokeRelativePath
    if (Test-Path -LiteralPath $smokePath) {
        $smokeItem = Get-Item -LiteralPath $smokePath -Force
        if (-not $smokeItem.PSIsContainer) {
            throw "预期 smoke 路径为目录：$smokePath"
        }

        Assert-NotReparsePoint $smokeItem
        foreach ($child in (Get-ChildItem -LiteralPath $smokePath -Force)) {
            if ($child.Name -ieq 'qb-notes.md') {
                continue
            }

            $relativePath = Join-Path $smokeRelativePath $child.Name
            Add-CleanupAction $relativePath "Smoke 运行产物 $relativePath"
        }
    }
}

if ($script:Actions.Count -eq 0) {
    Write-Host '没有找到匹配的测试产物。'
    return
}

Write-Host "清理范围：$Scope"
foreach ($action in $script:Actions) {
    Write-Host "  $($action.Description)"
    Write-Host "    $($action.Path)"
}

if (-not $Apply) {
    Write-Host '当前为预览模式；确认清单后加上 -Apply 执行删除。'
    return
}

if (-not $PSCmdlet.ShouldProcess(
        $script:RepositoryRoot,
        "删除范围 '$Scope' 中列出的生成产物")) {
    return
}

foreach ($action in $script:Actions) {
    Remove-Item -LiteralPath $action.Path -Recurse -Force -ErrorAction Stop
    Write-Host "已删除：$($action.Path)"
}
