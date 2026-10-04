using System;
using System.Linq;
using System.Net;

namespace FastGithub.DomainResolve
{
    /// <summary>
    /// IpAddressFilter 自检：确认新增的网段归属过滤能挡住污染段，
    /// 且**不误杀** GitHub / Fastly / Azure 的真实边缘 IP。
    /// </summary>
    public static class IpAddressFilterSelfTest
    {
        private static int failed;

        public static int Run()
        {
            // ---- 必须被拒：实测出现过的污染地址 ----
            Reject("173.252.108.21", "实测 github.global.ssl.fastly.net 被返回的 Meta IP");
            Reject("2a03:2880:f107:83:face:b00c:0:25de", "实测带 face:b00c 签名的 Meta IPv6");
            Reject("31.13.94.23", "历史记录的 Meta IP");
            Reject("157.240.1.1", "Meta 段内");
            Reject("31.6.1.1", "Meta 31.6.0.0/15 内");
            Reject("2a03:2880::1", "Meta IPv6 段边界");

            // ---- 必须放行：v2.6.3 实测日志中 GitHub 真实返回的 IP ----
            Accept("20.205.243.166", "github.com 真实 IP");
            Accept("20.205.243.168", "api.github.com 真实 IP");
            Accept("140.82.121.5", "github.com 真实 IP");
            Accept("140.82.114.4", "github.com 真实 IP");
            Accept("140.82.121.3", "github.com 种子 IP");
            Accept("140.82.112.21", "collector.github.com 真实 IP");
            Accept("140.82.113.25", "alive.github.com 真实 IP");
            Accept("20.26.156.215", "github.com 种子 IP");
            Accept("185.199.108.133", "raw.githubusercontent.com 种子 IP");
            Accept("185.199.109.133", "usercontent 实测 IP");
            Accept("185.199.110.133", "usercontent 实测 IP");
            Accept("185.199.111.133", "usercontent 实测 IP");
            Accept("185.199.109.215", "githubassets 实测 IP");
            Accept("185.199.111.215", "githubassets 实测 IP");
            Accept("185.199.108.215", "githubassets 实测 IP");
            Accept("151.101.53.194", "Fastly 实测 IP（曾被误判为污染）");
            Accept("199.232.17.194", "Fastly 实测 IP");
            Accept("4.225.11.194", "实测混入 github.com 的地址（归属 Azure，合法）");
            Accept("20.50.88.244", "visualstudio 实测 IP");
            Accept("20.50.88.238", "visualstudio 实测 IP");
            Accept("20.50.88.242", "visualstudio 实测 IP");

            // ---- IPv6 放行：Fastly / Cloudflare 正向段不能被误杀 ----
            Accept("2606:4700::1", "Cloudflare（Fastly 共用段，必须放行）");
            Accept("2a04:4e42::1", "Fastly IPv6");
            Accept("2400:cb00::1", "Cloudflare IPv6");

            // ---- 基础保留段仍应被拒 ----
            Reject("127.0.0.1", "环回");
            Reject("192.168.1.1", "私有网段");
            Reject("0.0.0.0", "Adblock 特征");
            Reject("10.0.0.1", "私有网段");
            Reject("::1", "IPv6 环回");

            Console.WriteLine(failed == 0
                ? "IpAddressFilter 自检全部通过"
                : $"IpAddressFilter 自检失败 {failed} 项");
            return failed;
        }

        private static void Reject(string ip, string reason)
        {
            var address = IPAddress.Parse(ip);
            var rejected = IpAddressFilter.IsInvalid(address);
            if (rejected)
            {
                Console.WriteLine($"  OK   拒绝 {ip,-42} ({reason})");
            }
            else
            {
                failed++;
                Console.WriteLine($"  FAIL 拒绝 {ip,-42} ({reason}) —— 实际被放行！");
            }
        }

        private static void Accept(string ip, string reason)
        {
            var address = IPAddress.Parse(ip);
            var accepted = IpAddressFilter.IsInvalid(address) == false;
            if (accepted)
            {
                Console.WriteLine($"  OK   放行 {ip,-42} ({reason})");
            }
            else
            {
                failed++;
                Console.WriteLine($"  FAIL 放行 {ip,-42} ({reason}) —— 被误杀！");
            }
        }
    }
}
