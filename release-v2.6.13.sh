#!/usr/bin/env bash
# v2.6.13 发布恢复入口：原 Token 失效后，设置新 GH_TOKEN 一键完成「推送 + CI 发布」。
# 用法：GH_TOKEN=<新Token> bash release-v2.6.13.sh
set -euo pipefail

REPO_DIR="D:/WorkBuddySpace/2026-10-02-13-08-19/FastGithubOps.UI"
PY="D:/Python/Python39/python.exe"
REPO="jsyzc2019/FastGithubOps.UI"
VERSION="v2.6.13"

: "${GH_TOKEN:?请先设置 GH_TOKEN=<新的 GitHub Personal Access Token（需要 repo + workflow 权限）>}"
cd "$REPO_DIR"

echo "=== 1/3 读取远端 main 基线 ==="
BASE=$(curl -sS -m 30 --ssl-no-revoke "https://api.github.com/repos/$REPO/branches/main" \
  | "$PY" -c "import sys,json; print(json.load(sys.stdin)['commit']['sha'])")
echo "remote main: $BASE"

echo "=== 2/3 用 Git Data API 推送本地 HEAD ==="
GH_TOKEN="$GH_TOKEN" "$PY" push_via_api.py "$BASE" HEAD

echo "=== 3/3 触发 release.yml（$VERSION） ==="
echo "{\"ref\":\"main\",\"inputs\":{\"version\":\"$VERSION\",\"prerelease\":false}}" > tmp_dispatch.json
curl -sS -m 30 --ssl-no-revoke -X POST \
  -H "Authorization: token $GH_TOKEN" \
  -H "Accept: application/vnd.github+json" \
  -H "Content-Type: application/json" \
  --data-binary "@tmp_dispatch.json" \
  "https://api.github.com/repos/$REPO/actions/workflows/release.yml/dispatches" \
  -w "dispatch HTTP=%{http_code}\n" -o /dev/null
rm -f tmp_dispatch.json

echo "完成：到 https://github.com/$REPO/actions 查看 Create Release 运行结果"
