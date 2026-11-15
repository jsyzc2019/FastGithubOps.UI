# -*- coding: utf-8 -*-
"""FastGithub 运行日志分析器（只读，不改动任何运行时文件）。

用法：
    python tools/log-analysis/analyze_logs.py [日志目录] [日期 YYYYMMDD ...]

默认日志目录 D:\\fastgithub-my\\logs，默认分析目录下最新 3 天的 log*.txt。
输出六段：日志构成 / 请求与失败率 / 故障码按小时 / 明文DNS噪声 / DoH 不可用窗口 /
候选池健康 / 单域名明细。口径见 docs/运行日志分析-20261115.md。
"""
import os
import re
import glob
import sys
import collections

TS = re.compile(r"^(\d{4}-\d{2}-\d{2})T(\d{2}):(\d{2}):(\d{2})\.\d+\+08:00\s+\[(\w+)\]$")
REQ = re.compile(r"^(\w+) (https?://[^\s]+) responded (\d+) in ([\d.]+) ms")
POOL = re.compile(r"^([\w.\-]+):(\d+)->\[(.*)\]$")


def entries(path):
    """按 Serilog 的“时间戳行 / logger 行 / 消息行 / 空行”结构切分条目。"""
    cur = None
    with open(path, "r", encoding="utf-8-sig", errors="replace") as f:
        for line in f:
            line = line.rstrip("\n")
            mt = TS.match(line)
            if mt:
                if cur:
                    yield cur
                cur = {"date": mt.group(1), "h": int(mt.group(2)),
                       "min": int(mt.group(3)), "lvl": mt.group(5),
                       "logger": "", "msg": []}
            elif cur is not None and line.strip():
                if not cur["logger"] and not cur["msg"]:
                    cur["logger"] = line.strip()
                else:
                    cur["msg"].append(line.strip())
    if cur:
        yield cur


def pct(vals, p):
    if not vals:
        return 0.0
    vals = sorted(vals)
    return vals[min(len(vals) - 1, int(len(vals) * p))]


def analyze(path):
    day = os.path.basename(path)[3:11]
    lvl = collections.Counter()
    logger = collections.Counter()
    req = 0
    code = collections.Counter()
    ok_lat, bad_lat = [], []
    err_hour = collections.Counter()
    refused_hour = collections.Counter()
    refused_dom = collections.Counter()
    doh_empty_hour = collections.Counter()
    doh_empty_times = []
    pool = collections.Counter()
    restart = []
    tele504 = 0

    for e in entries(path):
        msg = " ".join(e["msg"])
        lg = e["logger"].split(".")[-1]
        lvl[e["lvl"]] += 1
        logger[lg] += 1

        if "Refused" in msg and lg == "DnsClient":
            refused_hour[e["h"]] += 1
            host = msg.split("@")[0]
            refused_dom[host] += 1

        if "所有端点均不可用" in msg:
            doh_empty_hour[e["h"]] += 1
            doh_empty_times.append(e["h"] * 60 + e["min"])

        if "FastGithub 启动完成" in msg:
            restart.append("%02d:%02d %s" % (e["h"], e["min"], msg.split("版本为")[-1].strip(" =]")))

        m = POOL.match(msg.strip())
        if m and lg == "DomainResolver":
            pool[len([x for x in m.group(3).split(",") if x.strip()])] += 1

        m = REQ.match(msg)
        if not m:
            continue
        req += 1
        c = int(m.group(3))
        ms = float(m.group(4))
        code[c] += 1
        if c >= 400:
            err_hour[e["h"]] += 1
            if c in (502, 504):
                bad_lat.append(ms)
                if c == 504 and any(k in m.group(2) for k in
                                    ("/github/collect", "browser/rum", "browser/stats")):
                    tele504 += 1
        elif c != 101:
            ok_lat.append(ms)

    fails = code[502] + code[504]
    print("\n==== %s ====" % day)
    print("日志条目 %d ｜ 级别 %s" % (sum(lvl.values()), dict(lvl)))
    print("噪声占比：DnsClient %d 条 (%.1f%%)，其中 Refused %d 条"
          % (logger["DnsClient"], 100.0 * logger["DnsClient"] / max(1, sum(lvl.values())),
             sum(refused_dom.values())))
    print("请求 %d ｜ 502=%d 504=%d ｜ 真实失败率 %.2f%% ｜ 504 中遥测端点 %d 条 (%.0f%%)"
          % (req, code[502], code[504], 100.0 * fails / max(1, req), tele504,
             100.0 * tele504 / max(1, code[504])))
    print("成功耗时(剔101) p50=%.0f p90=%.0f p99=%.0f ms ｜ 失败耗时 p50=%.0f ms"
          % (pct(ok_lat, .5), pct(ok_lat, .9), pct(ok_lat, .99), pct(bad_lat, .5)))
    print("启动次数 %d：%s" % (len(restart), "; ".join(restart)))
    print("Refused/小时 %s" % dict(sorted(refused_hour.items())))
    print("DoH 全端点不可用/小时 %s" % dict(sorted(doh_empty_hour.items())))
    print("候选池大小分布 %s" % dict(sorted(pool.items())))
    print("502/504 按小时 %s" % dict(sorted(err_hour.items())))
    print("Refused 域名 Top5 %s" % refused_dom.most_common(5))

    # 把“全端点不可用”按 3 分钟间隔聚成簇，用于区分冷启动爆发与零散抖动
    clusters, cur = [], []
    for t in doh_empty_times:
        if cur and t - cur[-1] <= 3:
            cur.append(t)
        else:
            if cur:
                clusters.append(cur)
            cur = [t]
    if cur:
        clusters.append(cur)
    big = [c for c in clusters if len(c) >= 5]
    print("不可用簇 %d 个，其中 >=5 次的爆发簇：" % len(clusters))
    for c in big:
        print("   %02d:%02d~%02d:%02d %d 次" % (c[0] // 60, c[0] % 60, c[-1] // 60, c[-1] % 60, len(c)))


def main():
    log_dir = sys.argv[1] if len(sys.argv) > 1 else r"D:\fastgithub-my\logs"
    days = sys.argv[2:]
    files = sorted(glob.glob(os.path.join(log_dir, "log*.txt")))
    if days:
        files = [f for f in files if os.path.basename(f)[3:11] in days]
    else:
        files = files[-3:]
    for f in files:
        analyze(f)


if __name__ == "__main__":
    sys.setrecursionlimit(10000)
    main()
