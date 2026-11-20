using System;
using System.Net;

namespace FastGithub.Http
{
    public enum GitHubConnectionSource
    {
        Fixed,
        Dynamic,
        DynamicFallback
    }

    /// <summary>
    /// 最近一次 GitHub HTTPS 转发建连的实际地址和来源。
    /// </summary>
    public sealed class GitHubConnectionStatus
    {
        private readonly object gate = new();
        private Snapshot? latest;

        public Snapshot? Latest
        {
            get
            {
                lock (this.gate)
                {
                    return this.latest;
                }
            }
        }

        public sealed record Snapshot(IPAddress Address, GitHubConnectionSource Source, DateTimeOffset ConnectedAt);

        internal void Record(string host, IPAddress address, bool isFixed, bool hasConfiguredAddresses)
        {
            if (string.Equals(host, "github.com", StringComparison.OrdinalIgnoreCase) == false)
            {
                return;
            }

            var source = isFixed ? GitHubConnectionSource.Fixed
                : hasConfiguredAddresses ? GitHubConnectionSource.DynamicFallback
                : GitHubConnectionSource.Dynamic;
            lock (this.gate)
            {
                this.latest = new Snapshot(address, source, DateTimeOffset.UtcNow);
            }
        }
    }
}
