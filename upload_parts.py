#!/usr/bin/env python
"""逐个上传分卷附件，每卷独立重试，上传后回读 size 核对。"""
import glob
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
TAG = "v2.6.7"


def api(method, url, data=None, headers=None):
    req = urllib.request.Request(url, data=data, method=method)
    req.add_header("Authorization", f"token {T}")
    req.add_header("Accept", "application/vnd.github+json")
    req.add_header("User-Agent", "fg-upload")
    for k, v in (headers or {}).items():
        req.add_header(k, v)
    try:
        with urllib.request.urlopen(req, timeout=600) as r:
            b = r.read().decode("utf-8")
            return json.loads(b) if b else {}
    except urllib.error.HTTPError as e:
        raise RuntimeError(f"{method} {e.code}: {e.read().decode('utf-8','replace')[:200]}")


def sha256_of(p):
    h = hashlib.sha256()
    with open(p, "rb") as f:
        for c in iter(lambda: f.read(1 << 20), b""):
            h.update(c)
    return h.hexdigest()


def main():
    parts = sorted(glob.glob("fastgithub-win-x64-v267.zip.part*"))
    if not parts:
        print("未找到分卷")
        return 1

    rel = api("GET", f"{API}/releases/tags/{TAG}")
    upload_url = rel["upload_url"].split("{")[0]
    existing = {a["name"]: a["id"] for a in rel.get("assets", [])}

    # 清理旧的同名资产
    for name, aid in list(existing.items()):
        if name.startswith("fastgithub-win-x64-v267.zip"):
            print("删除旧资产:", name)
            api("DELETE", f"{API}/releases/assets/{aid}")
            del existing[name]

    ok_all = True
    for path in parts:
        name = os.path.basename(path)
        size = os.path.getsize(path)
        data = open(path, "rb").read()
        digest = sha256_of(path)

        for attempt in range(1, 6):
            try:
                t0 = time.time()
                out = api("POST", f"{upload_url}?name={name}", data, {
                    "Content-Type": "application/octet-stream",
                    "Content-Length": str(len(data)),
                })
                el = time.time() - t0
                same = out["size"] == size
                print(f"{name}  {size/1048576:.1f}MB  {el:.1f}s  size{'一致' if same else '不一致!'}")
                if not same:
                    ok_all = False
                break
            except Exception as e:
                print(f"{name} 第{attempt}次失败: {e}")
                time.sleep(attempt * 4)
        else:
            print(f"{name} 最终失败")
            ok_all = False

    # 最终回读
    rel = api("GET", f"{API}/releases/tags/{TAG}")
    names = sorted(a["name"] for a in rel.get("assets", []))
    print(f"\n远端附件 {len(names)} 个：")
    for a in rel.get("assets", []):
        print(f"  {a['name']}  {a['size']} 字节")
    return 0 if ok_all else 1


if __name__ == "__main__":
    sys.exit(main())
