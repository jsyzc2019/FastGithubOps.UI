#!/usr/bin/env python
"""
通过 GitHub Git Data API 推送提交。

用途：本机 github.com:443 不可达（git push over https 直接 TLS 失败），
但 api.github.com:443 可达，因此改用 API 直接构造 blob/tree/commit 并更新 ref。

用法：
    set GH_TOKEN=xxx
    python push_via_api.py <base_sha> <commit_sha> [<commit_sha> ...]

脚本按给出的顺序逐个提交，每个 commit 以其前一个结果作为 parent，
从而在远端还原出与本地一致的提交历史。
"""
import base64
import json
import os
import ssl
import subprocess
import sys
import urllib.error
import urllib.request

REPO = "jsyzc2019/FastGithubOps.UI"
API = f"https://api.github.com/repos/{REPO}"
TOKEN = os.environ.get("GH_TOKEN") or os.environ.get("GITHUB_TOKEN")

# 【v2.6.11】本机经 FastGithub 代理访问 api.github.com，2026-10-30 起
# Python(OpenSSL) 对该 MITM 链报 "unable to get issuer certificate"
# （curl/schannel 仍验证通过，仅吊销检查失败）。遇证书验证失败时
# 降级为跳过校验重试——与 curl 在同一链路上的信任级别一致。
_unverified_ctx = ssl._create_unverified_context()
_no_proxy_handler = urllib.request.ProxyHandler({})


def api(method, path, payload=None, verify=True, use_proxy=True):
    data = None
    if payload is not None:
        # ensure_ascii=False 保留中文；GitHub API 接受 UTF-8
        data = json.dumps(payload, ensure_ascii=False).encode("utf-8")
    req = urllib.request.Request(
        f"{API}{path}",
        data=data,
        method=method,
        headers={
            "Authorization": f"token {TOKEN}",
            "Accept": "application/vnd.github+json",
            "Content-Type": "application/json",
            "User-Agent": "fastgithub-push",
        },
    )
    try:
        handlers = []
        if use_proxy is False:
            handlers.append(_no_proxy_handler)
        if verify is False:
            handlers.append(urllib.request.HTTPSHandler(context=_unverified_ctx))
        opener = urllib.request.build_opener(*handlers)
        with opener.open(req, timeout=120) as resp:
            body = resp.read().decode("utf-8")
            return json.loads(body) if body else {}
    except urllib.error.HTTPError as e:
        detail = e.read().decode("utf-8", "replace")
        raise RuntimeError(f"{method} {path} -> {e.code}: {detail[:600]}") from None
    except (urllib.error.URLError, ssl.SSLError, OSError) as e:
        msg = str(e)
        if verify and ("CERTIFICATE" in msg.upper() or "certificate" in msg):
            return api(method, path, payload, verify=False, use_proxy=use_proxy)
        if use_proxy and "Tunnel connection failed" in msg:
            # 【v2.6.12】系统代理对 api.github.com 返回隧道 502（间歇出现）：
            # curl 直连可达，绕过代理重试
            return api(method, path, payload, verify=verify, use_proxy=False)
        raise


def git(*args):
    out = subprocess.run(
        ["git", *args],
        capture_output=True,
        cwd=os.path.dirname(os.path.abspath(__file__)),
    )
    if out.returncode != 0:
        raise RuntimeError(f"git {' '.join(args)} 失败: {out.stderr.decode('utf-8', 'replace')}")
    return out.stdout.decode("utf-8", "replace")


def changed_files(parent, commit):
    """返回 [(status, path)]，status 为 A/M/D/R 等"""
    # -c core.quotepath=false：中文等非 ASCII 路径不被转义成 \344\270... 形式，
    # 否则后续 git show 读不到该文件（v2.6.11 实测跳过了 docs/上游对照报告）
    raw = git("-c", "core.quotepath=false", "diff", "--name-status", "--no-renames", parent, commit)
    result = []
    for line in raw.splitlines():
        line = line.strip()
        if not line:
            continue
        parts = line.split("\t")
        if len(parts) < 2:
            continue
        result.append((parts[0][0], parts[-1]))
    return result


def main():
    if not TOKEN:
        print("缺少 GH_TOKEN 环境变量")
        return 1

    if len(sys.argv) < 3:
        print(__doc__)
        return 1

    base = sys.argv[1]
    commits = sys.argv[2:]

    parent_sha = base
    for commit in commits:
        message = git("log", "-1", "--format=%B", commit).strip()
        changes = changed_files(f"{commit}^", commit)

        tree_entries = []
        for status, path in changes:
            if status == "D":
                # 删除：把该路径的 sha 置为 null
                tree_entries.append({
                    "path": path,
                    "mode": "100644",
                    "type": "blob",
                    "sha": None,
                })
                continue

            try:
                content = git("show", f"{commit}:{path}")
            except RuntimeError:
                print(f"  跳过（无法读取）: {path}")
                continue

            # 二进制内容无法用 UTF-8 传输，走 base64
            try:
                blob = api("POST", "/git/blobs", {"content": content, "encoding": "utf-8"})
            except RuntimeError:
                raw = subprocess.run(
                    ["git", "show", f"{commit}:{path}"],
                    capture_output=True,
                    cwd=os.path.dirname(os.path.abspath(__file__)),
                ).stdout
                blob = api("POST", "/git/blobs", {
                    "content": base64.b64encode(raw).decode("ascii"),
                    "encoding": "base64",
                })

            tree_entries.append({
                "path": path,
                "mode": "100644",
                "type": "blob",
                "sha": blob["sha"],
            })

        if not tree_entries:
            print(f"{commit}: 无文件变更，跳过")
            continue

        tree = api("POST", "/git/trees", {
            "base_tree": parent_sha,
            "tree": tree_entries,
        })

        created = api("POST", "/git/commits", {
            "message": message,
            "tree": tree["sha"],
            "parents": [parent_sha],
        })

        api("PATCH", "/git/refs/heads/main", {"sha": created["sha"]})
        parent_sha = created["sha"]
        print(f"{commit} -> {created['sha'][:8]}  ({len(tree_entries)} 个文件)")

    print(f"\n完成，远端 main 现在指向 {parent_sha}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
