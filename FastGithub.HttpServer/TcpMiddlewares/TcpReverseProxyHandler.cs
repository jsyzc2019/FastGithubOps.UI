using FastGithub.DomainResolve;
using FastGithub.Http;
using Microsoft.AspNetCore.Connections;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace FastGithub.HttpServer.TcpMiddlewares
{
    /// <summary>
    /// tcp协议代理处理者
    /// </summary>
    abstract class TcpReverseProxyHandler : ConnectionHandler
    {
        private readonly IDomainResolver domainResolver;
        private readonly DnsEndPoint endPoint;
        private readonly TimeSpan connectTimeout = TimeSpan.FromSeconds(10d);

        /// <summary>
        /// tcp协议代理处理者
        /// </summary>
        /// <param name="domainResolver"></param>
        /// <param name="endPoint"></param>
        public TcpReverseProxyHandler(IDomainResolver domainResolver, DnsEndPoint endPoint)
        {
            this.domainResolver = domainResolver;
            this.endPoint = endPoint;
        }

        /// <summary>
        /// tcp连接后
        /// </summary>
        /// <param name="context"></param>
        /// <returns></returns>
        public override async Task OnConnectedAsync(ConnectionContext context)
        {
            var cancellationToken = context.ConnectionClosed;
            var (connection, address) = await CreateConnectionAsync(cancellationToken);
            using (connection)
            {
                try
                {
                    var task1 = connection.CopyToAsync(context.Transport.Output, cancellationToken);
                    var task2 = context.Transport.Input.CopyToAsync(connection, cancellationToken);
                    await Task.WhenAny(task1, task2);
                }
                catch (Exception)
                {
                    // 传输中途失败说明该IP虽能建连但不可用（对端重置、链路被掐）。
                    // 只在建连成功时上报成功会把这类IP洗白，导致下次仍选中它。
                    domainResolver.ReportFailure(endPoint, address);
                    throw;
                }
            }
        }

        /// <summary>
        /// 创建连接
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        /// <exception cref="AggregateException"></exception>
        private async Task<(Stream Connection, IPAddress Address)> CreateConnectionAsync(CancellationToken cancellationToken)
        {
            var innerExceptions = new List<Exception>();
            await foreach (var address in domainResolver.ResolveAsync(endPoint, cancellationToken))
            {
                var socket = TcpSocketFactory.Create(address.AddressFamily);
                try
                {
                    using var timeoutTokenSource = new CancellationTokenSource(connectTimeout);
                    using var linkedTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutTokenSource.Token);
                    await socket.ConnectAsync(address, endPoint.Port, linkedTokenSource.Token);

                    // TCP 反代此前完全不回写健康度：SSH/Git 走到坏IP时只是这次连接失败，
                    // 解析器仍把它当作"测速最快"继续排在首位，于是每次 git 操作都重试同一个坏IP。
                    domainResolver.ReportSuccess(endPoint, address);
                    return (new NetworkStream(socket, ownsSocket: true), address);
                }
                catch (Exception ex)
                {
                    socket.Dispose();
                    cancellationToken.ThrowIfCancellationRequested();
                    domainResolver.ReportFailure(endPoint, address);
                    innerExceptions.Add(ex);
                }
            }
            throw new AggregateException($"无法连接到{endPoint.Host}:{endPoint.Port}", innerExceptions);
        }
    }
}
