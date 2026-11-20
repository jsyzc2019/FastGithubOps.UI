<#
.SYNOPSIS
  一键测速选出 GitHub 最优 IP，并自动写入 FastGitHub 配置。
.DESCRIPTION
  从社区 hosts 源多源拉取 github.com 候选 IP（失败回退内置官方段），再逐个测 git 协议真实吞吐，
  自动备份并写入 appsettings.github.json 的 github.com.IPAddresses（吞吐前 3，按优先级降序）。
  用途：固定 IP 失效或变慢时，双击运行即可换新 IP，无需手改配置文件。
.NOTES
  运行方式：右键「使用 PowerShell 运行」本脚本，或双击同目录的 pick-fastest-ip.bat。
#>

$ErrorActionPreference = "Stop"
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch {}

# ---- 1. 定位配置文件 ----
$configFile = Join-Path $PSScriptRoot "appsettings\appsettings.github.json"
if (-not (Test-Path $configFile)) {
    # 兜底：默认安装路径
    $configFile = "D:\FsTools\fastgithub-win-x64\appsettings\appsettings.github.json"
}
if (-not (Test-Path $configFile)) {
    Write-Host "[错误] 找不到 appsettings.github.json" -ForegroundColor Red
    Write-Host "请把本脚本放到 FastGitHub 程序目录下（与 fastgithub.exe 同目录）。"
    Read-Host "按回车退出"
    exit 1
}
Write-Host "配置文件: $configFile"

# ---- 2. 候选 IP：多源动态拉取 github.com 的 IP + 硬编码兜底 ----
# 兜底池：所有源不可达时使用（GitHub 官方 git/web 段，可自行增删）
$fallbackCandidates = @(
    "20.6.7.16","20.6.7.17","20.27.163.208","20.205.243.166","20.207.73.82",
    "140.82.112.3","140.82.112.4","140.82.113.3","140.82.113.4",
    "140.82.114.3","140.82.114.4","140.82.121.3","140.82.121.4",
    "57.155.223.240","172.182.252.130"
)

# 社区 IP 源（按优先级），每个源只需提取 github.com 对应的 IP
$sources = @(
    @{ Name = "GitHub520";       Url = "https://raw.hellogithub.com/hosts.json"; Type = "json" },
    @{ Name = "FetchGHHosts";    Url = "https://hosts.gitcdn.top/hosts.txt";      Type = "text" },
    @{ Name = "GitHub-IP-hosts"; Url = "https://cdn.jsdelivr.net/gh/ittuann/GitHub-IP-hosts@main/hosts"; Type = "text" },
    @{ Name = "gh-hosts";        Url = "https://gitee.com/fliu2476/github-hosts/raw/main/hosts"; Type = "text" }
)

# 从所有源拉取 github.com 的 IP，合并去重；每个源独立失败不影响其他源
function Get-GithubComIPs {
    $ips = [System.Collections.Generic.List[string]]::new()
    foreach ($src in $sources) {
        try {
            $body = & curl.exe -s --max-time 10 -A "Mozilla/5.0" $src.Url 2>$null
            if ($LASTEXITCODE -ne 0 -or -not $body) { throw "拉取失败" }
            if ($src.Type -eq "json") {
                $arr = $body | ConvertFrom-Json
                foreach ($item in $arr) {
                    if ($item[1] -eq "github.com") { $ips.Add([string]$item[0]) }
                }
            } else {
                foreach ($line in ($body -split "`n")) {
                    if ($line -match "^\s*(\d{1,3}(\.\d{1,3}){3})\s+github\.com\s*$") {
                        $ips.Add($Matches[1])
                    }
                }
            }
            Write-Host ("  [源] {0} 命中" -f $src.Name) -ForegroundColor Green
        } catch {
            Write-Host ("  [源] {0} 不可达，跳过" -f $src.Name) -ForegroundColor DarkGray
        }
    }
    return $ips
}

$candidates = @(Get-GithubComIPs | Select-Object -Unique)
if (-not $candidates -or $candidates.Count -eq 0) {
    Write-Host "所有源不可达，回退到硬编码候选池（$($fallbackCandidates.Count) 个）" -ForegroundColor Yellow
    $candidates = $fallbackCandidates
} else {
    Write-Host ("多源合并得到 {0} 个 github.com 候选 IP" -f $candidates.Count)
}

# ---- 3. 测速 ----
Write-Host ""
Write-Host "开始测速（git 协议吞吐，约 30~60 秒）..." -ForegroundColor Cyan
$tmp = Join-Path $env:TEMP "fg-speed.tmp"
$results = @()
foreach ($ip in $candidates) {
    $out = & curl.exe -s -o $tmp -w "%{http_code} %{speed_download}" `
        --noproxy "*" --connect-timeout 4 -m 10 `
        --resolve "github.com:443:$ip" `
        "https://github.com/git/git/info/refs?service=git-upload-pack" 2>$null
    if ($out -and $out -match "^(\d+)\s+([\d\.]+)") {
        $code = $Matches[1]
        $speed = [double]$Matches[2]
        $color = if ($code -eq "200") { "Green" } else { "DarkGray" }
        Write-Host ("  {0,-16} 码:{1}  吞吐:{2,10:N0} B/s" -f $ip, $code, $speed) -ForegroundColor $color
        $results += [PSCustomObject]@{ IP = $ip; Code = $code; Speed = $speed }
    } else {
        Write-Host ("  {0,-16} 连不上" -f $ip) -ForegroundColor DarkGray
    }
}
Remove-Item $tmp -ErrorAction SilentlyContinue

# ---- 4. 选最优（取吞吐前 3 作为候选列表，按优先级降序）----
$top = $results | Where-Object { $_.Code -eq "200" } | Sort-Object Speed -Descending | Select-Object -First 3
if (-not $top) {
    Write-Host ""
    Write-Host "[失败] 没有测到 HTTP 200 的 IP，请检查网络后重试。" -ForegroundColor Red
    Read-Host "按回车退出"
    exit 1
}
Write-Host ""
$rank = 0
$top | ForEach-Object { $rank++; Write-Host ("  候选{0}: {1}（吞吐 {2:N0} B/s）" -f $rank, $_.IP, $_.Speed) -ForegroundColor Green }
Write-Host ("共选出 {0} 个候选 IP，将写入 IPAddresses 列表" -f $top.Count)

# ---- 5. 备份并写入 ----
$backup = "$configFile.bak-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
Copy-Item $configFile $backup -Force
Write-Host "已备份: $backup"

$raw = [System.IO.File]::ReadAllText($configFile)
$raw = $raw -replace "^\uFEFF", ""
$obj = $raw | ConvertFrom-Json
$github = $obj.FastGithub.DomainConfigs.'github.com'
if (-not $github) {
    Write-Host "[错误] 配置中没有 github.com 段" -ForegroundColor Red
    Read-Host "按回车退出"
    exit 1
}
# 写列表（覆盖旧值）；同时清理旧版单字段 IPAddress，避免"列表优先"时单字段被忽略造成混淆
$github.IPAddresses = @($top.IP)
if ($github.PSObject.Properties['IPAddress']) { $github.PSObject.Properties.Remove('IPAddress') }
$out = $obj | ConvertTo-Json -Depth 10
[System.IO.File]::WriteAllText($configFile, [char]0xFEFF + $out, (New-Object System.Text.UTF8Encoding $true))

Write-Host ("已写入 github.com.IPAddresses = [{0}]" -f ($top.IP -join ', ')) -ForegroundColor Green
Write-Host "配置会自动热重载；若未即时生效，重启 FastGitHub 即可。"
Write-Host ""
Read-Host "按回车退出"
