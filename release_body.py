#!/usr/bin/env python
"""生成 Release 正文与创建请求体（纯 ASCII JSON 转义，避免 API 400）。"""
import json
import sys

NOTES = """## FastGithub v2.5.0（仅 win-x64）

### 本版重点：连通性与速度

**修复发布包缺失 UI**
- v2.4.1 的包里只有 fastgithub.exe，FastGithub.UI.exe 与其 .config 全部丢失。
  现在主程序与 UI 发布到同一目录，并在 CI 中增加发布后校验，缺文件直接失败。

**候选 IP 从 1 个扩展到多个**
- 此前被在线 hosts 源覆盖的域名永远只有源里那唯一一个候选 IP，它一坏就没有任何备选。
  改为 hosts 源 IP 优先 + DNS 结果补充。实测（/connectivity）：
  - github.com 由不可达变为可达（267ms）
  - codeload.github.com 322ms -> 217ms（选到更快的 IP）
  - 可达域名数 3/6 -> 4/6

**测速改为 TLS 层验证**
- 原先只做 TCP 握手，会选出"连得上但用不了"的 IP（TLS 层被干扰时 TCP 层仍表现成功）。
  现在对 443/8443 端口追加真实 TLS 握手探测，且使用独立的超时预算。

**连接由串行改为并发赛马**
- 原先逐个尝试候选 IP，一个坏 IP 要等满超时才轮到下一个。
  现在并发发起、取第一个握手成功者，最坏耗时从 N x 超时降到 1 x 超时。

**TCP 参数统一调优**
- NoDelay = true：git/SSH 的小包交互流不再被 Nagle 攒包拖慢。
- 收发缓冲区 64KB -> 256KB：改善跨境大文件（release 附件、git pack）吞吐。
- HTTP 连接池：空闲连接保留 3 分钟，复用可省掉每次 TCP+TLS 往返。
- SSH/Git 反代补上健康度反馈，不再反复选中同一个坏 IP。

### 可诊断性
- 新增 /connectivity：主动探测 GitHub 各域名，返回是否可达、候选 IP 数、
  中选 IP 与握手耗时。支持 ?host=a,b 自定义目标。
- /diagnostics：CA 证书是否被信任、各端口实际监听值、IP 健康度快照。
- 修复 UI 与主程序的端口静契约：UI 硬编码 45678 而主程序动态选端口，
  端口被占时 UI 的流量图表与"更新IP"会静默失效。

### 使用
解压后运行 FastGithub.UI.exe，首次使用请以管理员身份运行以自动导入 CA 证书。
"""

if __name__ == "__main__":
    tag = sys.argv[1]
    payload = {
        "tag_name": tag,
        "name": f"FastGithub {tag}",
        "body": NOTES,
        "draft": False,
        "prerelease": False,
    }
    with open(sys.argv[2], "w", encoding="ascii") as f:
        json.dump(payload, f, ensure_ascii=True)
    print(f"written: {sys.argv[2]}")
