# FastGithubOps.UI

> 在 [yuan71058/FastGithub](https://github.com/yuan71058/FastGithub) v2.3.1 基础上，
> 与 [docmirror/dev-sidecar](https://github.com/docmirror/dev-sidecar) 逐项对比后「取长补短」的优化版本。
> 目标框架升级至 **.NET 10 (net10.0)**，已构建并验证 **win-x64 自包含单文件**可执行程序。

本软件仅用于学习与交流，不具备「翻墙」能力，请在遵守当地法律法规的前提下使用。使用本软件产生的一切后果由使用者自行承担。

## 这个项目改了什么

原版 FastGithub 的典型症状是「启动正常、日志正常，但访问 GitHub 依然慢或不通」。根因是
**IP 选择只有测速、没有反馈闭环**：某个 IP 一旦被测速判为快，即便它后来连不通了，
也会在 5 分钟缓存期内被持续优先选中。

本次把 dev-sidecar 的三块核心能力移植进来，同时保留 FastGithub 透明拦截、轻量、零配置的优势。

| 能力 | 来源 | 落地位置 |
| --- | --- | --- |
| IP 健康度反馈闭环（成功率 + 连续失败计数 + 拉黑） | dev-sidecar `DynamicChoice` | `FastGithub.DomainResolve/IpHealthTracker.cs`（新增） |
| 无效/污染 IP 过滤（0.0.0.0、私有/保留网段、已知污染地址） | dev-sidecar `isZeroIp` | `FastGithub.DomainResolve/IpAddressFilter.cs`（新增） |
| 单 IP 探测超时 5s、失败即换下一个 | dev-sidecar `SpeedTester` | `FastGithub.Http/HttpClientHandler.cs` |
| 系统代理通道作为驱动失效时的兜底 | dev-sidecar 系统代理模式 | `FastGithub/Startup.cs` + `FastGithub.HttpServer/KestrelServerExtensions.cs` |

### 详细改动清单

1. **`IpHealthTracker.cs`（新增）** — 按 `域名|IP` 统计成功率与连续失败数；
   `连续失败 >= 1` 或 `成功率 < 0.4` 立即拉黑 2 分钟；成功后自动解除拉黑；
   提供 `GetPenalty()` 参与排序（健康度优先于时延）。
2. **`IpAddressFilter.cs`（新增）** — 过滤 `0.0.0.0`、127/8、10/8、100.64/10、169.254/16、
   172.16/12、192.168/16、198.18/15、TEST-NET、组播与保留段，以及 IPv6 的
   `::` / `fc00::/7` / `fe80::/10` / `2002::`，外加 24 个公开整理的已知污染 IP。
3. **`IDomainResolver`** — 新增 `ReportSuccess` / `ReportFailure` / `IsBlacklisted`，
   在接口层建立「真实请求结果 → IP 选择」的反馈通道。
4. **`DomainResolver`** — 实现上述接口；当**首选 IP** 被拉黑时触发一次重选
   （单域名 30 秒冷却，防止失败风暴打满 DNS）。
5. **`IPAddressService`** — 排序由「时延」改为「健康度优先、时延其次」；
   剔除已拉黑 IP；若全部被拉黑则退化为按健康度排序，保证不会无 IP 可用。
6. **`DnsClient`** — 解析结果统一走 `IpAddressFilter`；移除重复 yield 加密 DNS 的死代码
   （第二次必然命中同一份缓存）；输出过滤计数日志。
7. **`HttpClientHandler`** — 连接成功（HTTPS 时含 TLS 握手）回写成功、失败回写失败；
   跳过已拉黑 IP 不再浪费一次超时等待；单 IP 连接超时 **10s → 5s**
   （3 个 IP 全坏的最坏耗时从 30s 降到 15s）。
8. **`Startup` / `KestrelServerExtensions`** — Windows 上**额外监听 `127.0.0.1:38457` 正向代理**，
   端口被占用时降级告警而非崩溃，作为透明拦截失效时的可用通道。
9. **`DnsInterceptHostedService` / `TcpInterceptHostedService`** — WinDivert 异常不再终止整个进程
   （原版直接 `host.StopAsync()` 退出），改为**降级存活**并提示手动设置系统代理。
10. **`FastGithubOptions`** — 抽出 `DefaultHttpProxyPort` 常量（38457）。
11. **工程化修复** — TFM `net7.0 → net10.0`（net7 已于 2024-05 EOL，net8/net9 于 2026-11 EOL，
    net10 为 LTS 至 2028-11）；`7.0.0-rc*` 浮动版本 → 固定 `10.0.0`（RC 包已下架，无法还原）；
    新增 `NuGet.config` 显式声明包源；版本号 `2.3.1 → 2.4.0`。

完整的对比矩阵、根因诊断与构建记录见 [`docs/优化对比报告.html`](docs/优化对比报告.html)。

## 构建

需要 **.NET 10 SDK**（`dotnet --list-sdks` 能看到 `10.0.x` 即可）。

```bat
:: 仅构建后端（win-x64 自包含单文件）
dotnet publish FastGithub/FastGithub.csproj -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -m:1 -nr:false -o publish/win-x64

:: 构建托盘 UI（net45）
dotnet build FastGithub.UI/FastGithub.UI.csproj -c Release -m:1 -nr:false
```

或直接运行仓库根目录的 `build.bat`（会清理后构建并把 `FastGithub.UI.exe` 拷进 `publish/`）。

产物说明：

| 文件 | 说明 |
| --- | --- |
| `fastgithub.exe` | 主程序，win-x64 自包含单文件，目标机**无需安装 .NET** |
| `FastGithub.UI.exe` | Windows 托盘 UI（net45） |
| `appsettings/` | 9 份域名配置（github / google / microsoft / fastly …） |
| `dnscrypt-proxy/` | 加密 DNS 组件 |

## 发布

维护者可通过 GitHub Actions 创建 Release，或在 Windows 上使用 `release-local.ps1` 演练打包及正式发布。版本准备、产物校验和重试步骤见 [发布流程](docs/本地发布流程.md)，各版本正文维护在 [release-notes](release-notes/README.md)。

## 运行

- **Windows 桌面模式**。打开 `FastGithub.UI.exe`，按 Windows 提示确认管理员权限。托盘程序检查实际权限后启动后端，后端继承管理员权限，用于 WinDivert 透明拦截和系统证书安装。
- **正向代理兜底**。驱动无法加载时，可以将客户端的 HTTP 代理设为 `127.0.0.1:38457`。
- **服务模式**：`fastgithub start` / `fastgithub stop` 安装与卸载 Windows 服务。
- **手动刷新 IP**：访问 `http://127.0.0.1:45678/refresh-ip`。
  该端口由程序按占用情况动态选取（`GlobalListener.UiHttpPort`），
  **以启动日志中打印的实际端口为准**；托盘 UI 会自动使用正确端口。
- **证书**。首次启动在 `cacert/` 生成自签 CA 并尝试装入「受信任的根证书颁发机构」。系统证书安装失败后，会尝试当前用户的证书存储。

### Windows 登录自启

1. 以管理员身份将完整程序包解压到受保护的本地目录，例如 `C:\Program Files\FastGithubOps.UI`。普通用户可写的目录无法用于管理员自启。
2. 使用要自动启动托盘的管理员账户打开 `FastGithub.UI.exe`。在托盘菜单的「设置」勾选「登录时以管理员身份启动」，点击「确定」。也可同时开启「启动后最小化」。
3. 该管理员账户下次登录 Windows 时，任务计划程序以最高权限启动托盘和后端。首次启用时需要管理员授权，后续登录由 Windows 执行已注册的任务。

从 v2.6.16 等旧版本升级时，在新版设置中勾选并保存一次。新版在确认计划任务已注册后移除该账户的旧 `HKCU\Run` 项。取消勾选并保存可关闭自启。移动或卸载前先关闭自启，换目录后在新位置重新启用。

以管理员身份打开 PowerShell 可核对当前账户的任务。应看到 `Highest`、`Interactive`、登录触发器和完整的程序路径。注销并重新登录后，在任务管理器「详细信息」中打开「已提升」列，确认托盘和后端进程均显示「是」。

```powershell
$sid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$task = Get-ScheduledTask -TaskName "FastGithub.UI-$sid"
$task.Principal | Select-Object UserId, LogonType, RunLevel
$task.Triggers | Select-Object UserId, Enabled, CimClass
$task.Actions | Select-Object Execute, Arguments, WorkingDirectory
```

任务在当前管理员账户的交互式登录会话中启动。若使用另一个账户的管理员凭据运行程序，自启任务属于那个管理员账户。相关权限和触发规则见 [Microsoft 任务计划程序文档](https://learn.microsoft.com/en-us/windows/win32/taskschd/security-contexts-for-running-tasks)。

## 验证记录

win-x64 自包含单文件版本已实际启动验证：

```
已监听 https://localhost:443，https反向代理服务启动完成
已监听 http://localhost:80，http反向代理服务启动完成
已监听 ssh://localhost:22，github的ssh反向代理服务启动完成
已监听 git://localhost:9418，github的git反向代理服务启动完成
已监听 http://localhost:38457，http代理服务启动完成
======[ FastGithub 启动完成，当前版本为 V2.4.0 ]======
```

编译结果：构建通过（0 错误）；已知告警来自 net10 对过时 API 的提示，不影响运行。
如需复核，请在本地执行下节构建命令并查看输出。

## 后续可做

- 测速加入 TLS 层验证（当前仍是纯 TCP 探测），进一步压缩「能连不能用」的 IP 生存空间。
- 把各 IP 的成功率暴露到 `/flowStatistics`，便于排查。
- `KeepErrorThreshold` / `MinSuccessRate` / `BlacklistDuration` 下沉到 `appsettings.json`。
- 用 `X509CertificateLoader` 替换已过时的 `X509Certificate2(string/byte[])` 构造，清零告警。

## 致谢与许可

- 基座：[yuan71058/FastGithub](https://github.com/yuan71058/FastGithub)（上游源自 dotnetcore/FastGithub）
- 参照：[docmirror/dev-sidecar](https://github.com/docmirror/dev-sidecar)
- 许可：MIT（见 [LICENSE](LICENSE)）
