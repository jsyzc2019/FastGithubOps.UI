#Requires -Version 5.1
<#
.SYNOPSIS
    在 Windows 本地构建 win-x64 发布包，核对 GitHub 草稿资产后公开发布。

.PARAMETER Version
    vX.Y.Z 格式的版本号，必须与 Directory.Build.props 和 release-notes/<版本>.md 一致。

.PARAMETER Repo
    GitHub.com 目标仓库（owner/repo），默认 FluxForceDynamics/FastGithubOps.UI。

.PARAMETER Target
    正式发布使用的完整提交 SHA，必须等于当前 HEAD。默认取当前 HEAD。

.PARAMETER OutDir
    本次构建和打包的输出目录，指定路径必须尚不存在。默认在系统临时目录下为本次运行创建新目录。

.PARAMETER NoPublish
    只构建、校验和打包，不访问 GitHub 发布 API。允许构建工作区中的改动。

.EXAMPLE
    .\release-local.ps1 -Version v2.6.15 -NoPublish
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('(?-i)\Av[0-9]+\.[0-9]+\.[0-9]+\z')]
    [string]$Version,

    [ValidatePattern('\A[A-Za-z0-9-]+/[A-Za-z0-9_.-]+\z')]
    [string]$Repo = 'FluxForceDynamics/FastGithubOps.UI',

    [ValidatePattern('\A[0-9a-fA-F]{40}\z')]
    [string]$Target,

    [string]$OutDir,

    [switch]$NoPublish
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
# 显式检查本机命令的退出码，使脚本不受调用方的 PowerShell 7 偏好设置影响。
$PSNativeCommandUseErrorActionPreference = $false

function Write-Step {
    param([string]$Message)

    Write-Host
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Get-CleanHead {
    $head = & git rev-parse --verify HEAD
    if ($LASTEXITCODE -ne 0 -or $head -notmatch '\A[0-9a-f]{40}\z') {
        throw '无法取得当前 Git 提交的完整 SHA'
    }

    $changes = & git status --porcelain --untracked-files=all
    if ($LASTEXITCODE -ne 0) {
        throw '无法检查 Git 工作区状态'
    }
    if ($changes) {
        throw '正式发布需要干净的 Git 工作区。请提交改动或使用 -NoPublish 演练'
    }
    return $head
}

function Invoke-GitHubApi {
    param([string[]]$ApiArguments)

    $response = & gh api --hostname github.com @ApiArguments
    if ($LASTEXITCODE -ne 0) {
        throw "GitHub API 请求失败（$($ApiArguments[0])，gh 退出码 $LASTEXITCODE）"
    }
    return $response
}

if ($OutDir) {
    $OutDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutDir)
} else {
    $runId = [Guid]::NewGuid().ToString('N')
    $OutDir = Join-Path ([System.IO.Path]::GetTempPath()) "fastgithub-release-$Version-$runId"
}

Push-Location -LiteralPath $PSScriptRoot
try {
    Write-Step '检查构建和发布条件'
    if (Test-Path -LiteralPath $OutDir) {
        throw "输出路径已存在，请指定新目录：$OutDir"
    }
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw '未找到 dotnet，请安装 .NET 10 SDK'
    }

    $propsPath = Join-Path $PSScriptRoot 'Directory.Build.props'
    [xml]$props = Get-Content -LiteralPath $propsPath -Raw -Encoding UTF8
    $versionNode = $props.SelectSingleNode('/Project/PropertyGroup/Version')
    if ($null -eq $versionNode -or $versionNode.InnerText.Trim() -cne $Version.Substring(1)) {
        throw "版本 $Version 与 Directory.Build.props 的 <Version> 不一致"
    }

    $notesPath = Join-Path $PSScriptRoot "release-notes/$Version.md"
    if (-not (Test-Path -LiteralPath $notesPath -PathType Leaf)) {
        throw "缺少发布说明：release-notes/$Version.md"
    }

    $repoApi = "repos/$Repo"
    $tagRef = "refs/tags/$Version"
    if (-not $NoPublish) {
        if (-not (Get-Command git -ErrorAction SilentlyContinue) -or -not (Get-Command gh -ErrorAction SilentlyContinue)) {
            throw '正式发布需要 Git 和已登录的 GitHub CLI'
        }
        $head = Get-CleanHead
        if ($Target -and $Target -ine $head) {
            throw "Target 必须等于当前 HEAD；当前提交为 $head"
        }
        $Target = $head

        if ((Invoke-GitHubApi @($repoApi, '--jq', '.permissions.push')) -ne 'true') {
            throw "当前 GitHub 凭据缺少 $Repo 的写权限"
        }
        $remoteHead = Invoke-GitHubApi @("$repoApi/commits/$Target", '--jq', '.sha')
        if ($remoteHead -ine $Target) {
            throw "目标仓库中找不到提交 $Target，请先推送该提交"
        }

        # 匹配接口在没有同名引用时成功返回空列表，网络及权限错误仍会中断。
        $tagRefs = Invoke-GitHubApi @("$repoApi/git/matching-refs/tags/$Version", '--jq', '.[].ref')
        if ($tagRefs -ccontains $tagRef) {
            throw "远程已存在标签 $Version"
        }
        $releaseTags = Invoke-GitHubApi @("$repoApi/releases", '--paginate', '--jq', '.[].tag_name')
        if ($releaseTags -ccontains $Version) {
            throw "远程已存在 $Version 的 Release 或草稿"
        }
    }

    # 不带 -Force，目录被其他进程抢先创建时同样中断。
    New-Item -ItemType Directory -Path $OutDir | Out-Null
    Write-Host "输出目录：$OutDir"

    # 临时使用 nuget.org，避免仓库配置的镜像返回损坏的 nupkg。
    $nugetConfig = Join-Path $OutDir 'NuGet.org.config'
    @'
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
</configuration>
'@ | Set-Content -LiteralPath $nugetConfig -Encoding UTF8

    $binDir = Join-Path $OutDir 'bin'
    $publishArguments = @('-c', 'Release', '--artifacts-path', (Join-Path $OutDir 'artifacts'),
        "-p:RestoreConfigFile=$nugetConfig", '-o', $binDir)

    Write-Step '构建主程序（net10.0 / win-x64 自包含单文件）'
    & dotnet publish FastGithub/FastGithub.csproj @publishArguments -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false
    if ($LASTEXITCODE -ne 0) {
        throw '主程序构建失败'
    }

    Write-Step '构建 UI（net45 / WPF）'
    & dotnet publish FastGithub.UI/FastGithub.UI.csproj @publishArguments
    if ($LASTEXITCODE -ne 0) {
        throw 'UI 构建失败'
    }

    Write-Step '校验并打包发布文件'
    $executables = @('fastgithub.exe', 'FastGithub.UI.exe')
    $requiredFiles = $executables + @('FastGithub.UI.exe.config', 'appsettings.json',
        'dnscrypt-proxy/dnscrypt-proxy.exe', 'dnscrypt-proxy/dnscrypt-proxy.toml')
    foreach ($fileName in $requiredFiles) {
        $filePath = Join-Path $binDir $fileName
        if (-not (Test-Path -LiteralPath $filePath -PathType Leaf) -or (Get-Item -LiteralPath $filePath).Length -eq 0) {
            throw "发布文件缺失或为空：$fileName"
        }
    }

    $shaFile = Join-Path $OutDir 'fastgithub-win-x64.sha256'
    $shaLines = foreach ($fileName in $executables) {
        $hash = (Get-FileHash -LiteralPath (Join-Path $binDir $fileName) -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $fileName"
    }
    Set-Content -LiteralPath $shaFile -Value $shaLines -Encoding ASCII

    # cacert 可能含本机 CA 私钥。打包时排除它和日志目录。
    $archiveEntries = Get-ChildItem -LiteralPath $binDir | Where-Object { $_.Name -notin @('cacert', 'logs') }
    $zipFile = Join-Path $OutDir 'fastgithub-win-x64.zip'
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::Open($zipFile, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        $archiveFiles = Get-ChildItem -LiteralPath $archiveEntries.FullName -Recurse -File
        foreach ($file in $archiveFiles) {
            # 显式使用 ZIP 路径分隔符，避免 Windows PowerShell 5.1 写入反斜杠。
            $entryName = $file.FullName.Substring($binDir.Length + 1).Replace('\', '/')
            $null = [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $archive, $file.FullName, $entryName, [System.IO.Compression.CompressionLevel]::Optimal)
        }
    } finally {
        $archive.Dispose()
    }
    Write-Host "$zipFile"
    Write-Host "$shaFile"

    if ($NoPublish) {
        return
    }

    if ((Get-CleanHead) -cne $Target) {
        throw '构建期间 HEAD 已变更，请重新构建'
    }

    $localAssets = foreach ($assetPath in @($zipFile, $shaFile)) {
        $assetFile = Get-Item -LiteralPath $assetPath
        $hash = (Get-FileHash -LiteralPath $assetPath -Algorithm SHA256).Hash.ToLowerInvariant()
        [PSCustomObject]@{
            Name = $assetFile.Name
            Path = $assetFile.FullName
            Size = $assetFile.Length
            Digest = "sha256:$hash"
        }
    }

    Write-Step '创建 Release 草稿'
    $draftId = Invoke-GitHubApi @("$repoApi/releases", '--method', 'POST',
        '-f', "tag_name=$Version", '-f', "target_commitish=$Target",
        '-f', "name=FastGithubOps.UI $Version", '-F', "body=@$notesPath", '-F', 'draft=true', '--jq', '.id')
    if ($draftId -notmatch '\A[1-9][0-9]*\z') {
        throw "未获取到 $Version 的草稿 ID，请检查 $Repo 的 Releases"
    }

    $draftApi = "$repoApi/releases/$draftId"
    try {
        Write-Step "上传并核对草稿 $draftId 的资产"
        foreach ($asset in $localAssets) {
            $uploadUrl = "https://uploads.github.com/${draftApi}/assets?name=$($asset.Name)"
            Invoke-GitHubApi @($uploadUrl, '--method', 'POST', '--input', $asset.Path,
                '--header', 'Content-Type: application/octet-stream', '--silent')
        }

        $draft = Invoke-GitHubApi @($draftApi,
            '--jq', '{draft,tag_name,target_commitish,assets:[.assets[] | {name,state,size,digest}]}') | ConvertFrom-Json
        if ($draft.draft -ne $true -or $draft.tag_name -cne $Version -or $draft.target_commitish -ine $Target) {
            throw '草稿状态、标签或目标提交与本次构建不符'
        }
        if ($draft.assets.Count -ne $localAssets.Count) {
            throw "草稿资产数量不符，期望 $($localAssets.Count) 项，实际 $($draft.assets.Count) 项"
        }
        foreach ($local in $localAssets) {
            $matching = @($draft.assets | Where-Object { $_.name -ceq $local.Name })
            if ($matching.Count -ne 1) {
                throw "草稿中找不到唯一的资产：$($local.Name)"
            }
            $remote = $matching[0]
            if ($remote.state -cne 'uploaded' -or $remote.size -ne $local.Size -or $remote.digest -ine $local.Digest) {
                throw "草稿资产的上传状态、大小或 SHA-256 校验失败：$($local.Name)"
            }
            Write-Host "核对一致：$($local.Name)（$($local.Size) 字节）"
        }

        Write-Step '创建标签并公开 Release'
        # GitHub 会拒绝创建已存在的引用，避免并发发布时复用指向其他提交的同名标签。
        Invoke-GitHubApi @("$repoApi/git/refs", '--method', 'POST', '-f', "ref=$tagRef",
            '-f', "sha=$Target", '--silent')
        $published = Invoke-GitHubApi @($draftApi, '--method', 'PATCH', '-F', 'draft=false',
            '--jq', '{draft,tag_name,html_url}') | ConvertFrom-Json
        if ($published.draft -ne $false -or $published.tag_name -cne $Version -or -not $published.html_url) {
            throw 'GitHub 未确认 Release 已公开'
        }
        Write-Host "发布完成：$($published.html_url)" -ForegroundColor Green
    } catch {
        throw "发布中断，请检查 $Repo 的 Release $draftId 和标签 $Version。$($_.Exception.Message)"
    }
} finally {
    Pop-Location
}
