#!/usr/bin/env python
"""单文件上传 Release 附件（不分卷）。

【为什么不再分卷】v2.6.7 曾把 53MB 附件切成 8 卷上传，根因被误判为"GitHub 附件上传有
30 秒硬上限"。审计 v2.6.7 实测日志（publish/v267/logs/log20261004.txt）后确认：
那 11 个 504 的耗时**全部精确落在 30010~30061ms**，即 30.000s —— 这是本项目
appsettings.github.json 里 `*.github.com` 的 `Timeout=00:00:30` 掐断的，
由 YARP 映射成 504（与 v2.6.2 定位过的 `RequestTimedOut` 同源），
**不是 GitHub 服务端的限制**。同一时段 8 个 6.6MB 分卷里有 6 个成功（9.6~14s），
说明链路本身完全健康，只是单文件超过 30s 预算。

v2.6.8 已把 `uploads.github.com` 单独配为 `Timeout=00:20:00`（精确条目在
DomainPattern 排序中优先于 `*.github.com`），因此本脚本按整文件一次上传。
上传后回读 size 与 digest 双重校验。
"""
import hashlib
import json
import os
import sys
import time
import urllib.error
import urllib.request

# Token 只从环境变量读取。绝不可硬编码进仓库文件 ——
# GitHub 的 secret scanning 会在 push 时以 422 "Secret detected in content" 直接拒绝。
T = os.environ.get("GH_TOKEN") or os.environ.get("GITHUB_TOKEN")
if not T:
    print("缺少 GH_TOKEN 环境变量")
    sys.exit(1)

API = "https://api.github.com/repos/jsyzc2019/FastGithubOps.UI"


def api(method, url, data=None, headers=None, timeout=1800):
    req = urllib.request.Request(url, data=data, method=method)
    req.add_header("Authorization", f"token {T}")
    req.add_header("Accept", "application/vnd.github+json")
    req.add_header("User-Agent", "fg-upload")
    for k, v in (headers or {}).items():
        req.add_header(k, v)
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            b = r.read().decode("utf-8")
            return json.loads(b) if b else {}
    except urllib.error.HTTPError as e:
        raise RuntimeError(f"{method} {e.code}: {e.read().decode('utf-8', 'replace')[:300]}")


def sha256_of(p):
    h = hashlib.sha256()
    with open(p, "rb") as f:
        for c in iter(lambda: f.read(1 << 20), b""):
            h.update(c)
    return h.hexdigest()


def upload_one(upload_url, path, attempts=4):
    """上传单个文件；整体重试（网络抖动时重传即可，GitHub 会自行去重）。"""
    name = os.path.basename(path)
    size = os.path.getsize(path)
    digest = sha256_of(path)
    print(f"待上传 {name}  {size} 字节 ({size / 1048576:.1f}MB)  sha256={digest}")

    for attempt in range(1, attempts + 1):
        try:
            with open(path, "rb") as f:
                data = f.read()
            t0 = time.time()
            out = api("POST", f"{upload_url}?name={name}", data, {
                "Content-Type": "application/octet-stream",
                "Content-Length": str(len(data)),
            })
            el = time.time() - t0
            size_ok = out["size"] == size
            remote_digest = (out.get("digest") or "").replace("sha256:", "")
            digest_ok = (not remote_digest) or remote_digest == digest
            print(f"上传完成 {el:.1f}s  size{'一致' if size_ok else '不一致!'}  "
                  f"digest{'一致' if digest_ok else '不一致!'}")
            if size_ok and digest_ok:
                return True
            print("校验未通过，准备重试")
        except Exception as e:
            print(f"第 {attempt}/{attempts} 次失败: {e}")
            if attempt < attempts:
                # 退避后重试；注意不要短于 FastGithub 对 uploads.github.com 的失败退避
                time.sleep(min(15 * attempt, 60))
    return False


def main():
    if len(sys.argv) < 3:
        print("用法: upload_asset.py <tag> <文件路径> [旧资产名前缀...]")
        return 2

    tag = sys.argv[1]
    path = sys.argv[2]
    stale_prefixes = sys.argv[3:] or [os.path.basename(path)]

    if not os.path.isfile(path):
        print(f"文件不存在: {path}")
        return 1

    rel = api("GET", f"{API}/releases/tags/{tag}")
    upload_url = rel["upload_url"].split("{")[0]

    # 清理旧资产（含历史分卷），保证 Release 页面干净
    for a in rel.get("assets", []):
        if any(a["name"].startswith(p) for p in stale_prefixes):
            print("删除旧资产:", a["name"])
            api("DELETE", f"{API}/releases/assets/{a['id']}")

    ok = upload_one(upload_url, path)

    # 最终回读，确认远端状态
    rel = api("GET", f"{API}/releases/tags/{tag}")
    print(f"\n远端附件 {len(rel.get('assets', []))} 个：")
    for a in rel.get("assets", []):
        print(f"  {a['name']}  {a['size']} 字节  {a.get('state')}")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
