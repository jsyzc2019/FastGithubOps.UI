#!/usr/bin/env python
"""
把已校验的发布包按字节切分成 <=7MB 的分卷，供 GitHub 附件上传。

起因：GitHub 附件上传有**硬性 30 秒网关上限**。实测16/24/32/53MB 全部在
30.1s 返回 504RequestTimedOut，而 8MB（7.5s）成功 —— 限制的是上传耗时而非体积。
Release 附件 API 不支持分块 PUT，只能客户端切分。

原理：把完整 zip 按字节等分。拼接后与原文件**字节完全相同**，
因此中央目录里的偏移量依然正确，`7za t` 可正常校验。
"""
import hashlib
import os
import sys

SRC = "fastgithub-win-x64-v267.zip"
PREFIX = "fastgithub-win-x64-v267.zip"
LIMIT = 7 * 1024 * 1024


def sha256_of(p):
    h = hashlib.sha256()
    with open(p, "rb") as f:
        for c in iter(lambda: f.read(1 << 20), b""):
            h.update(c)
    return h.hexdigest()


def main():
    if not os.path.isfile(SRC):
        print(f"缺少 {SRC}")
        return 1
    total = os.path.getsize(SRC)
    src_sha = sha256_of(SRC)
    n = (total + LIMIT - 1) // LIMIT
    print(f"源文件 {SRC}: {total} 字节 ({total/1048576:.1f} MB)")
    print(f"sha256: {src_sha}")
    print(f"切分为 {n} 卷，每卷 <= {LIMIT/1048576:.0f} MB\n")

    names = []
    with open(SRC, "rb") as f:
        for i in range(n):
            name = f"{PREFIX}.part{i+1:02d}"
            left = LIMIT
            with open(name, "wb") as out:
                while left > 0:
                    buf = f.read(min(1 << 20, left))
                    if not buf:
                        break
                    out.write(buf)
                    left -= len(buf)
            names.append(name)
            print(f"  {name}  {os.path.getsize(name)} 字节")

    # 自校验：拼接后必须与源文件字节一致
    joined = hashlib.sha256()
    for name in names:
        with open(name, "rb") as f:
            for c in iter(lambda: f.read(1 << 20), b""):
                joined.update(c)
    ok = joined.hexdigest() == src_sha
    print(f"\n拼接后 sha256 一致: {ok}")
    if not ok:
        print("分卷有误，已中止")
        return 1
    print("分卷文件：")
    for name in names:
        print(name)
    return 0


if __name__ == "__main__":
    sys.exit(main())
