# 第三方组件声明（THIRD-PARTY NOTICES）

本项目（FastGithubOps.UI）以 MIT 许可发布。以下第三方组件随源码一并分发，
各自受其自身许可条款约束。MIT 与 ISC 均要求保留版权声明与许可全文，
故在此集中列明。

本项目基于 [yuan71058/FastGithub](https://github.com/yuan71058/FastGithub) 衍生
（上游源自 dotnetcore/FastGithub），两者均为 MIT 许可。

---

## 1. LiveCharts / LiveCharts.Wpf

- **文件**：`FastGithub.UI/Resource/LiveCharts.dll`、`FastGithub.UI/Resource/LiveCharts.Wpf.dll`
- **版本**：0.9.7（2017-06-20 发布）
- **许可**：MIT
- **版权**：Copyright (c) 2016 Alberto Rodriguez Orozco 与 LiveCharts 贡献者
- **来源**：<https://github.com/LiveCharts/LiveCharts>

> 说明：LiveCharts 1.x / 2.x 曾调整许可条款，但**本项目携带的是 0.9.7**，
> 该版本为 MIT。核对方式：DLL 文件版本资源为 `0.9.7`，且字节长度
> （217,600 / 152,064）与 NuGet 包 `lib/net40` 内的文件完全一致。

```
The MIT License (MIT)

Copyright (c) 2016 Alberto Rodriguez & LiveCharts contributors

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

---

## 2. Newtonsoft.Json

- **文件**：`FastGithub.UI/Resource/Newtonsoft.Json.dll`
- **许可**：MIT
- **版权**：Copyright (c) 2007 James Newton-King
- **来源**：<https://github.com/JamesNK/Newtonsoft.Json>

> 许可声明原文见随库分发的 `Newtonsoft.Json.dll` 内的 `LICENSE.TXT` 资源，
> 与 <https://www.newtonsoft.com/json> 所载条款一致。MIT 许可全文同上。

---

## 3. dnscrypt-proxy

- **文件**：`@dnscrypt-proxy/` 目录下的 `dnscrypt-proxy.toml`、各平台可执行文件
  （`win-x64/dnscrypt-proxy.exe`、`linux-x64/`、`linux-arm64/`、`osx-x64/`、`osx-arm64/`）
- **许可**：ISC
- **版权**：Copyright (c) 2013-2021 DNSCrypt developers
- **来源**：<https://github.com/DNSCrypt/dnscrypt-proxy>
- **许可全文**：随 `@dnscrypt-proxy/LICENSE` 一并分发

> `dnscrypt-proxy.toml` 为上游官方示例配置，未做改动。
> 其中引用的解析器清单文件附带 minisig 签名与官方公钥，未被篡改。

```
ISC License

Copyright (c) 2013-2021, DNSCrypt developers

Permission to use, copy, modify, and/or distribute this software for any
purpose with or without fee is hereby granted, provided that the above
copyright notice and this permission notice appear in all copies.

THE SOFTWARE IS PROVIDED "AS IS" AND THE AUTHOR DISCLAIMS ALL WARRANTIES WITH
REGARD TO THIS SOFTWARE INCLUDING ALL IMPLIED WARRANTIES OF MERCHANTABILITY
AND FITNESS. IN NO EVENT SHALL THE AUTHOR BE LIABLE FOR ANY SPECIAL, DIRECT,
INDIRECT, OR CONSEQUENTIAL DAMAGES OR ANY DAMAGES WHATSOEVER RESULTING FROM
LOSS OF USE, DATA OR PROFITS, WHETHER IN AN ACTION OF CONTRACT, NEGLIGENCE OR
OTHER TORTIOUS ACTION, ARISING OUT OF OR IN CONNECTION WITH THE USE OR
PERFORMANCE OF THIS SOFTWARE.
```

---

## 4. 其余随库分发的二进制文件

| 文件 | 说明 |
| --- | --- |
| `FastGithub.UI/app.ico` | 应用图标，本项目自有素材 |
| `Resources/MacOSXConfig/*.png` | 文档配图，本项目自有素材 |

---

## 审计说明

上述 11 个二进制文件均继承自上游 `yuan71058/FastGithub`，非本项目新增
（已与上游快照逐一 MD5 比对，结果全部一致）。若未来替换或升级其中任何组件，
请同步更新本文件。