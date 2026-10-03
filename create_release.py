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

    notes = """## FastGithub v2.5.0（仅 win-x64）

### 本版重点：连通性与速度

**修复发布包缺失 UI**
- v2.4.1 的包里只有 `fastgithub.exe`，`FastGithub.UI.exe` 与其 `.config` 全部丢失。
  现在主程序与 UI 发布到同一目录，并在 CI 中增加发布后校验，缺文件直接失败。

**候选 IP 从 1 个扩展到多个**
- 此前被在线 hosts 源覆盖的域名永远只有源里那唯一一个候选 IP，它一坏就没有任何备选。
  改为 hosts 源 IP 优先 + DNS 结果补充。实测（`/connectivity`）：
  - `github.com` 由不可达变为可达（267ms）
  - `codeload.github.com` 322ms → 217ms（选到更快的 IP）
  - 可达域名数 3/6 → 4/6

**测速改为 TLS 层验证**
- 原先只做 TCP 握手，会选出「连得上但用不了」的 IP（TLS 层被干扰时 TCP 层仍表现成功）。
  现在对 443/8443 端口追加真实 TLS 握手探测，且使用独立的超时预算。

**连接由串行改为并发赛马**
- 原先逐个尝试候选 IP，一个坏 IP 要等满超时才轮到下一个。
  现在并发发起、取第一个握手成功者，最坏耗时从 N×超时降到 1×超时。

**TCP 参数统一调优**
- `NoDelay = true`：git/SSH 的小包交互流不再被 Nagle 攒包拖慢。
- 收发缓冲区 64KB → 256KB：改善跨境大文件（release 附件、git pack）吞吐。
- HTTP 连接池：空闲连接保留 3 分钟，复用可省掉每次 TCP+TLS 往返。
- SSH/Git 反代补上健康度反馈，不再反复选中同一个坏 IP。

### 可诊断性
- 新增 `/connectivity`：主动探测 GitHub 各域名，返回是否可达、候选 IP 数、
  中选 IP 与握手耗时。支持 `?host=a,b` 自定义目标。
- `/diagnostics`：CA 证书是否被信任、各端口实际监听值、IP 健康度快照。
- 修复 UI 与主程序的端口静契约：UI 硬编码 45678 而主程序动态选端口，
  端口被占时 UI 的流量图表与「更新IP」会静默失效。

### 使用
解压后运行 `FastGithub.UI.exe`，首次使用请以管理员身份运行以自动导入 CA 证书。
"""

    release = request("POST", f"{API}/releases", {
        "tag_name": tag,
        "name": f"FastGithub {tag}",
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
