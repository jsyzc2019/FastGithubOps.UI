#!/usr/bin/env python
"""
创建 GitHub Release 并上传附件，上传后回读核对 size 与 sha256。

回读核对是必须的：曾经出现过上传后 GitHub 返回的 digest 与本地不一致
（链路被注入）而当时没有校验，导致发出去的包是损坏的。
"""
import hashlib
import json
import os
import sys
import urllib.error
import urllib.request

OWNER = "jsyzc2019"
REPO = "FastGithubOps.UI"
API = f"https://api.github.com/repos/{OWNER}/{REPO}"
UPLOADS = "https://uploads.github.com"
TOKEN = os.environ.get("GH_TOKEN") or os.environ.get("GITHUB_TOKEN")


def request(method, url, data=None, headers=None):
    req = urllib.request.Request(url, data=data, method=method)
    req.add_header("Authorization", f"token {TOKEN}")
    req.add_header("Accept", "application/vnd.github+json")
    req.add_header("User-Agent", "fastgithub-release")
    for k, v in (headers or {}).items():
        req.add_header(k, v)
    try:
        with urllib.request.urlopen(req, timeout=300) as resp:
            body = resp.read().decode("utf-8", "replace")
            return json.loads(body) if body else {}
    except urllib.error.HTTPError as e:
        detail = e.read().decode("utf-8", "replace")
        raise RuntimeError(f"{method} {url} -> {e.code}: {detail[:800]}") from None


def sha256_of(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


def main():
    if not TOKEN:
        print("缺少 GH_TOKEN")
        return 1

    tag = sys.argv[1]
    files = sys.argv[2:]

    notes = """## FastGithubOps.UI v2.5.6（仅 win-x64）

### 机制性回退：不再被反复阻断

**问题**
v2.5.0~v2.5.3 在部分网络下反复被阻断，体感明显差于基线 v2.3.1。
根因不在超时，而在自设机制：单次请求并发向 4 个 GitHub 边缘 IP 惊扰出完整 TLS
会话后丢弃，叠加后台每秒一轮的 TLS 探测风暴，形似端口扫描而被对端限流。

**修复**
- 删除请求层并发赛马，改回串行尝试（`MAX_TRY_COUNT = 3`），并加 20s 总建连预算。
- IP 探测从 TLS 握手降级为纯 TCP 握手，探测频率与强度回到基线水平。
- 失败时不再清空探测缓存（此前会形成「连不上→清缓存→全量重探→更像攻击」的
  正反馈死循环），改为失效 DNS 解析缓存。

### 恢复速度：被阻断后尽快可用

- 恢复链路四个环节串联压缩：健康度拉黑阈值 3→2、黑名单时长 5min→30s、
  刷新冷却 30s→10s、解析缓存主动失效。
- 5xx 自动重试一次（退避 800ms）。
- `DnsClient.InvalidateCache()` 同时失效明文 DNS 与 DoH 缓存。

### 两个隐性bug（实测确认修复）

- **DoH 报文字节序**：原用 `BinaryWriter.Write(ushort)` 写 DNS 报文，那是小端序，
  而 DNS 协议要求大端，导致四个 DoH 端点全部返回 HTTP 400、DoH 形同虚设。
  改为显式逐字节大端写入后，5 个域名 A/AAAA 全部 200。
- **UI 内部端口恒为 0**：`AppPorts.UiHttpBaseUrl` 声明在 `UiHttpPort` 之前且用插值
  初始化，C# 静态成员按声明顺序初始化，导致流量图与「更新IP」永远请求
  `http://127.0.0.1:0/...`。改为惰性属性后恢复。

### 其他

- hosts 源接入 IP 有效性过滤，修复 `avatars.githubusercontent.com -> 127.0.0.1`
  这类污染地址被当作真实 IP 写入。
- 流量图读取失败时不再静默吞异常，界面直接显示异常类型，便于定位。

### 实测对比（同一网络环境）

| 指标 | v2.5.5 | v2.5.6 |
|---|---|---|
| DoH 解析成功 | 0 | 53 |
| DoH 解析失败 | 33 | 0 |
| HTTP 200 | 28 | 157 |
| `[ERR]` 日志 | 0 | 0 |

### 使用

解压后运行 `FastGithub.UI.exe`，首次使用请以管理员身份运行以自动导入 CA 证书。
"""

    release = request("POST", f"{API}/releases", {
        "tag_name": tag,
        "target_commitish": os.environ.get("TARGET_COMMIT", "main"),
        "name": f"FastGithubOps.UI {tag}",
        "body": notes,
        "draft": False,
        "prerelease": False,
    })
    print(f"Release 已创建: {release['html_url']}")

    upload_url = release["upload_url"].split("{")[0]

    print("\n上传附件：")
    all_ok = True
    for path in files:
        name = os.path.basename(path)
        size = os.path.getsize(path)
        digest = sha256_of(path)

        with open(path, "rb") as f:
            data = f.read()

        # 必须显式给 Content-Length：否则 urllib 会改用 chunked 编码，
        # 而它对 bytes body 的 chunk 拼接有缺陷（TypeError: can't concat str to bytes）。
        asset = request("POST", f"{upload_url}?name={name}", data=data,
                        headers={
                            "Content-Type": "application/octet-stream",
                            "Content-Length": str(len(data)),
                        })
        print(f"  已上传 {name} ({size} 字节)")

        remote_size = asset.get("size")
        remote_digest = (asset.get("digest") or "").replace("sha256:", "")

        size_ok = remote_size == size
        digest_ok = remote_digest == digest
        if size_ok and digest_ok:
            print(f"    核对一致 sha256={digest[:16]}...")
        else:
            all_ok = False
            print(f"    !! 不一致: size {remote_size} vs {size}, digest {remote_digest} vs {digest}")

    print("\n附件核对全部一致" if all_ok else "\n存在不一致，请检查")
    return 0 if all_ok else 1


if __name__ == "__main__":
    sys.exit(main())
