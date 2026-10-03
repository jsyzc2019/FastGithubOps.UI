using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

namespace FastGithub.DomainResolve
{
    /// <summary>
    /// IP地址过滤器
    /// <para>
    /// 参考 dev-sidecar 的 <c>isZeroIp</c>（过滤 0.0.0.0 / :: 这类 Adblock 拦截特征）
    /// 并补齐 DNS 污染地址过滤。原 FastGithub 对解析结果只做了 loopback 过滤，
    /// 国内 DNS 返回的污染地址会直接进入测速队列——它们往往"握手很快"，
    /// 于是被稳定选中、稳定失败，是"装了没效果"的重要原因之一。
    /// </para>
    /// </summary>
    public static class IpAddressFilter
    {
        /// <summary>
        /// 已知的DNS污染IP（来自公开整理，命中即丢弃）
        /// </summary>
        private static readonly HashSet<string> knownPoisonIps = new(StringComparer.Ordinal)
        {
            "203.98.7.65", "159.106.121.75", "243.185.187.39", "8.7.198.45",
            "37.61.54.158", "46.82.174.68", "59.24.3.173", "78.16.49.15",
            "93.46.8.89", "128.121.126.139", "180.168.41.175", "188.5.4.96",
            "197.4.4.4", "203.161.230.171", "207.12.88.98", "209.36.73.33",
            "211.94.66.147", "213.169.251.35", "216.221.188.182", "216.234.179.13",
            "23.253.183.71", "249.129.46.48", "253.157.14.165", "192.30.255.113",
        };

        /// <summary>
        /// 【v2.6.4】按**网段归属**过滤：明确不属于 GitHub / Fastly / Azure 边缘的整段地址。
        /// <para>
        /// 【为什么不能只靠 IP 黑名单】逐个 IP 拉黑**永远无法收敛**：
        /// 污染源的返回地址每次都可能不同，日志里同一域名
        /// <c>github.global.ssl.fastly.net</c> 先后返回过 <c>173.252.108.21</c>、
        /// <c>151.101.53.194</c>、<c>199.232.17.194</c>。黑名单只能挡住手头这几个，
        /// 下一轮换一个就又漏进来（v2.6.3 实测就是这样：污染地址进了候选池，
        /// 靠下游 TCP+TLS 探测与健康度闭环才淘汰掉，白白多花建连与重解析成本）。
        /// <para>
        /// 【为什么按网段可行】GitHub 与其 CDN（Fastly、Azure、LiveJournal）的边缘节点
        /// 只可能落在若干**固定的、公开的**网段内。反过来，有几段地址是**明确属于
        /// 境外社交/广告网络**的，GFW 的 DNS 污染惯用它们作为伪造应答地址 ——
        /// 这类地址**在物理上不可能**是 GitHub 的边缘节点，因此可以整段拒绝。
        /// 这是"白名单式排除"，与网段大小无关，收敛且不会误杀。
        /// <para>
        /// 【判据】下面的网段均取自各机构公开的地址分配表（ARIN/RIPE/APNIC），
        /// 归属明确且长期稳定；若将来某段被重新分配，移除对应条目即可。
        /// </para>
        /// </summary>
        private static readonly byte[][] foreignIpv4NetBlocks = new[]
        {
            // ---- Meta / Facebook（AS32934）----
            // 这是 GFW DNS 污染最常用的伪造应答源。日志实测 v2.6.3 中
            // github.global.ssl.fastly.net 被返回 173.252.108.21 与
            // 2a03:2880:f107:83:face:b00c:0:25de —— 后者的 "face:b00c"
            // 就是 Facebook 的标志性 IPv6 签名，等于自带身份声明。
            MakeNetBlock("173.252.0.0"),      // /16
            MakeNetBlock("31.13.0.0"),        // /16
            MakeNetBlock("31.6.0.0"),         // /15
            MakeNetBlock("157.240.0.0"),      // /16
            MakeNetBlock("179.60.192.0"),     // /22
            MakeNetBlock("185.60.216.0"),     // /22
            MakeNetBlock("102.132.96.0"),     // /20
            MakeNetBlock("163.70.128.0"),     // /17
        };

        /// <summary>
        /// 各 IPv4 网段的前缀长度，与 <see cref="foreignIpv4NetBlocks"/> 一一对应。
        /// </summary>
        private static readonly int[] foreignIpv4PrefixLengths = new[]
        {
            16, 16, 15, 16, 22, 22, 20, 17,
        };

        /// <summary>
        /// Meta/Facebook 的 IPv6 段 2a03:2880::/32。
        /// 污染应答里常出现 <c>2a03:2880:...:face:b00c:...</c> 这种带 "face:b00c"
        /// 签名的地址，是最典型的污染特征。
        /// </summary>
        private static readonly byte[] metaIPv6Prefix = new byte[]
        {
            0x2a, 0x03, 0x28, 0x80,
        };

        private const int MetaIPv6PrefixLength = 32;

        /// <summary>
        /// 把点分十进制网段转成字节数组，避免每次判定都做字符串解析
        /// </summary>
        private static byte[] MakeNetBlock(string network)
        {
            return System.Net.IPAddress.Parse(network).GetAddressBytes();
        }

        /// <summary>
        /// 是否落在"明确不属于 GitHub"的境外网段内
        /// </summary>
        private static bool IsForeignNetBlock(IPAddress address)
        {
            var bytes = address.GetAddressBytes();

            if (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                // 2a03:2880::/32
                if (MatchPrefix(bytes, metaIPv6Prefix, MetaIPv6PrefixLength))
                {
                    return true;
                }

                // 2606:4700::/32（Cloudflare 正向）不在此列 —— Fastly 用了它，必须放行。
                return false;
            }

            if (address.AddressFamily != AddressFamily.InterNetwork)
            {
                return false;
            }

            for (var i = 0; i < foreignIpv4NetBlocks.Length; i++)
            {
                if (MatchPrefix(bytes, foreignIpv4NetBlocks[i], foreignIpv4PrefixLengths[i]))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 判断地址是否以指定前缀开头。
        /// </summary>
        /// <remarks>
        /// 【注意】<paramref name="network"/> 允许是**比地址更短**的前缀片段：
        /// IPv6 地址有 16 字节，而前缀通常只给出前 4 字节（如 <c>2a03:2880::/32</c>）。
        /// 早期实现要求两者长度严格相等，结果所有 IPv6 前缀都被静默拒绝 ——
        /// 这个错误由 <c>IpAddressFilterSelfTest</c> 抓出，肉眼很难发现。
        /// </remarks>
        /// <param name="address">待判定地址字节</param>
        /// <param name="network">网段首地址字节（长度可短于 address）</param>
        /// <param name="prefixLength">前缀长度（位）</param>
        private static bool MatchPrefix(byte[] address, byte[] network, int prefixLength)
        {
            if (network.Length > address.Length || prefixLength > network.Length * 8)
            {
                return false;
            }

            var fullBytes = prefixLength / 8;
            var remainingBits = prefixLength % 8;

            for (var i = 0; i < fullBytes; i++)
            {
                if (address[i] != network[i])
                {
                    return false;
                }
            }

            if (remainingBits > 0)
            {
                var mask = (byte)(0xFF << (8 - remainingBits));
                if ((address[fullBytes] & mask) != (network[fullBytes] & mask))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// 判定是否应丢弃该解析结果
        /// </summary>
        /// <param name="address">IP</param>
        /// <returns>true表示应丢弃</returns>
        public static bool IsInvalid(IPAddress address)
        {
            if (address == null)
            {
                return true;
            }

            if (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                return IsInvalidIPv6(address);
            }

            if (address.AddressFamily != AddressFamily.InterNetwork)
            {
                return true;
            }

            var bytes = address.GetAddressBytes();

            // 0.0.0.0 —— Adblock / 拦截特征
            if (bytes[0] == 0)
            {
                return true;
            }
            // 127.0.0.0/8
            if (bytes[0] == 127)
            {
                return true;
            }
            // 10.0.0.0/8
            if (bytes[0] == 10)
            {
                return true;
            }
            // 100.64.0.0/10（运营商级NAT）
            if (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127)
            {
                return true;
            }
            // 169.254.0.0/16
            if (bytes[0] == 169 && bytes[1] == 254)
            {
                return true;
            }
            // 172.16.0.0/12
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
            {
                return true;
            }
      // 192.168.0.0/16（私有网段）
            if (bytes[0] == 192 && bytes[1] == 168)
            {
                return true;
            }
            // 192.0.0.0/24（IETF协议保留）、192.0.2.0/24（TEST-NET-1）、
            // 192.88.99.0/24（6to4中继，RFC 7526 已废弃）
            if (bytes[0] == 192 && bytes[2] == 0 && (bytes[1] == 0 || bytes[1] == 2 || bytes[1] == 88))
            {
                return true;
            }
     // 198.18.0.0/15（基准测试保留段）
            if (bytes[0] == 198 && (bytes[1] == 18 || bytes[1] == 19))
            {
                return true;
            }
            // 198.51.100.0/24（TEST-NET-2）
            if (bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100)
            {
                return true;
            }
            // 203.0.113.0/24(TEST-NET-3)
            if (bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113)
            {
                return true;
            }
            // 224.0.0.0/4（组播）+ 240.0.0.0/4（保留，含255.255.255.255）
            if (bytes[0] >= 224)
            {
                return true;
            }

            // 【v2.6.4】整段拒绝明确属于境外社交/广告网络的地址。
            // 放在最后判断，避免与上面的保留段规则重复解释。
            if (IsForeignNetBlock(address))
            {
                return true;
            }

            return knownPoisonIps.Contains(address.ToString());
        }

        /// <summary>
        /// 判定IPv6是否应丢弃
        /// </summary>
        private static bool IsInvalidIPv6(IPAddress address)
        {
            var bytes = address.GetAddressBytes();

            // :: 与 ::1
            if (address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.IPv6Loopback))
            {
                return true;
            }
            // fc00::/7 与 fe80::/10（唯一本地地址与链路本地）
            if ((bytes[0] & 0xFE) == 0xFC || (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80))
            {
                return true;
            }
            // ff00::/8（组播）
            if (bytes[0] == 0xFF)
            {
                return true;
            }
            // 2002::/16（6to4）不是有效公网出口
            if (bytes[0] == 0x20 && bytes[1] == 0x02)
            {
                return true;
            }

            // ::ffff:0:0/96（IPv4映射地址）—— 必须单独判定。
            // 已知污染地址常以 ::ffff:203.98.7.65 这样的映射形式经 AAAA 记录返回，
            // 而 knownPoisonIps 只收录 IPv4 字面量；不在此处拆回 IPv4 递归判定，
            // 污染地址就能完整绕过整个过滤器。
            if (bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[10] == 0xFF && bytes[11] == 0xFF)
            {
                var mapped = new byte[4];
                Array.Copy(bytes, 12, mapped, 0, 4);
                return IsInvalid(new IPAddress(mapped));
            }

            // ::/96（IPv4兼容地址，已废弃）与 64:ff9b::/96（NAT64）
            if ((bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0x00 && bytes[3] == 0x00)
                || (bytes[0] == 0x00 && bytes[1] == 0x64 && bytes[2] == 0xFF && bytes[3] == 0x9B))
            {
                return true;
            }

            // 【v2.6.4】整段拒绝明确属于境外社交/广告网络的 IPv6（当前为 Meta 2a03:2880::/32）。
            // 注意不能一刀切 Cloudflare 的 2606:4700::/32 —— Fastly 的边缘节点就在其中。
            if (IsForeignNetBlock(address))
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// 过滤一批IP
        /// </summary>
        public static IEnumerable<IPAddress> Filter(IEnumerable<IPAddress> addresses)
        {
            foreach (var address in addresses)
            {
                if (IsInvalid(address) == false)
                {
                    yield return address;
                }
            }
        }
    }
}
