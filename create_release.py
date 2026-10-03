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

# Release 正文的兜底文案。正常路径应读取 release-notes/<tag>.md，
# 仅当该文件缺失时才用这份内嵌副本，保证脚本可独立运行。
FALLBACK_NOTES = """## FastGithubOps.UI（仅 win-x64）

解压后运行 `FastGithub.UI.exe`，首次使用请以管理员身份运行以自动导入 CA 证书。

本版本的完整变更说明见 `release-notes/` 目录。
"""


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

    # Release 正文优先取 release-notes/<tag>.md（单一事实来源，可版本管理）；
    # 缺失时回退到脚本内嵌的 FALLBACK_NOTES。
    notes_path = os.path.join(
        os.path.dirname(os.path.abspath(__file__)), "release-notes", f"{tag}.md"
    )
    if os.path.isfile(notes_path):
        with open(notes_path, encoding="utf-8") as f:
            notes = f.read()
        print(f"Release 正文取自 {notes_path}")
    else:
        notes = FALLBACK_NOTES
        print(f"警告：未找到 {notes_path}，回退到脚本内嵌说明")


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
