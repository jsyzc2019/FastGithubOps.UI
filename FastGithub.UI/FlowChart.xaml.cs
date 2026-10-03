using LiveCharts;
using LiveCharts.Configurations;
using LiveCharts.Wpf;
using Newtonsoft.Json;
using System;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Controls;

namespace FastGithub.UI
{
    /// <summary>
    /// FlowChart.xaml 的交互逻辑
    /// </summary>
    public partial class FlowChart : UserControl
    {
        private readonly LineSeries readSeries = new LineSeries
        {
            Title = "上行速率",
            PointGeometry = null,
            LineSmoothness = 1D,
            Values = new ChartValues<RateTick>()
        };

        private readonly LineSeries writeSeries = new LineSeries()
        {
            Title = "下行速率",
            PointGeometry = null,
            LineSmoothness = 1D,
            Values = new ChartValues<RateTick>()
        };

        private static DateTime GetDateTime(double timestamp) => new DateTime(1970, 1, 1).Add(TimeSpan.FromMilliseconds(timestamp)).ToLocalTime();

        private static double GetTimestamp(DateTime dateTime) => dateTime.ToUniversalTime().Subtract(new DateTime(1970, 1, 1)).TotalMilliseconds;


        public SeriesCollection Series { get; } = new SeriesCollection(Mappers.Xy<RateTick>().X(item => item.Timestamp).Y(item => item.Rate));

        public Func<double, string> XFormatter { get; } = timestamp => GetDateTime(timestamp).ToString("HH:mm:ss");

        public Func<double, string> YFormatter { get; } = value => $"{FlowStatistics.ToNetworkSizeString((long)value)}/s";

        public FlowChart()
        {
            InitializeComponent();

            this.Series.Add(this.readSeries);
            this.Series.Add(this.writeSeries);

            this.DataContext = this;

            // 【关键修复】构造函数在 UI 线程同步执行，而 InitFlowChartAsync 内
            // 第一处 await（HttpClient.GetAsync）之后的代码段运行在线程池线程。
            // 原实现在那里直接写 this.textBlockRead.Text 与 readSeries.Values，
            // 全是 WPF 依赖线程亲和性的对象：
            //   - TextBlock 跨线程赋值抛 InvalidOperationException("该线程没有访问此对象")；
            //   - LiveCharts 的 ChartValues 在非 UI 线程 Add 不会触发重绘，且可能抛线程异常。
            // 一旦抛异常，async void 的异常无法被外层 catch 捕获，UI 上就表现为
            // "流量图始终空白、连失败提示都没有"（服务端 /flowStatistics 实测返回 200 正常）。
            // 改为 Loaded 事件触发（确保在 UI 线程且控件已加载完成），并在每次
            // 写 UI 前用 Dispatcher.InvokeAsync 切回 UI 线程。
            this.Loaded += async (sender, e) => await this.InitFlowChartAsync();
        }

        /// <summary>
        /// 在 UI 线程上执行操作
        /// </summary>
        private void InvokeOnUi(Action action)
        {
            if (this.Dispatcher.CheckAccess() == false)
            {
                // 已关闭时不再排队，否则会抛 TaskCanceledException/InvalidOperationException
                if (this.Dispatcher.HasShutdownStarted == false)
                {
                    this.Dispatcher.InvokeAsync(action);
                }
                return;
            }
            action();
        }

        private async Task InitFlowChartAsync()
        {
            var consecutiveFailures = 0;
            while (this.Dispatcher.HasShutdownStarted == false)
            {
                try
                {
                    // 每轮新建 HttpClient：带 Timeout 的 HttpClient 一旦超时，
                    // 后续所有请求都会直接抛 TaskCanceledException 而不再真正发起请求，
                    // 复用它会让"一次抖动 → 图表永久停更"。
                    using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5d) };
                    await this.FlushFlowStatisticsAsync(httpClient);
                    consecutiveFailures = 0;
                }
                catch (Exception ex)
                {
                    // 【不要静默吞掉异常】原实现 catch(Exception){} 完全无反馈，
                    // 流量图不显示时无法区分"确实没流量"与"接口连不上/端口不对"，
                    // 排查只能靠猜。改为首次失败即在界面上给出可见提示，
                    // 连续失败时降低提示频率（避免每刷一次刷屏）。
                    if (consecutiveFailures == 0 || consecutiveFailures % 30 == 0)
                    {
                        var message = ex.GetType().Name;
                        this.InvokeOnUi(() =>
                        {
                            this.textBlockRead.Text = "流量读取失败";
                            this.textBlockWrite.Text = message;
                        });
                    }
                    consecutiveFailures++;
                }
                finally
                {
                    await Task.Delay(TimeSpan.FromSeconds(1d));
                }
            }
        }

        private async Task FlushFlowStatisticsAsync(HttpClient httpClient)
        {
            var response = await httpClient.GetAsync($"{AppPorts.UiHttpBaseUrl}/flowStatistics");
            var json = await response.EnsureSuccessStatusCode().Content.ReadAsStringAsync();
            var flowStatistics = JsonConvert.DeserializeObject<FlowStatistics>(json);
            if (flowStatistics == null)
            {
                return;
            }

            // 【关键】await 之后已离开 UI 线程，所有 UI/图表写入必须切回 UI 线程，
            // 否则 TextBlock 赋值抛线程异常、ChartValues.Add 不重绘（详见构造函数注释）。
            this.InvokeOnUi(() =>
            {
                this.textBlockRead.Text = FlowStatistics.ToNetworkSizeString(flowStatistics.TotalRead);
                this.textBlockWrite.Text = FlowStatistics.ToNetworkSizeString(flowStatistics.TotalWrite);

                var timestamp = GetTimestamp(DateTime.Now);
                this.readSeries.Values.Add(new RateTick(flowStatistics.ReadRate, timestamp));
                this.writeSeries.Values.Add(new RateTick(flowStatistics.WriteRate, timestamp));

                if (this.readSeries.Values.Count > 60)
                {
                    this.readSeries.Values.RemoveAt(0);
                    this.writeSeries.Values.RemoveAt(0);
                }
            });
        }

        private class RateTick
        {
            public double Rate { get; }

            public double Timestamp { get; }

            public RateTick(double rate, double timestamp)
            {
                this.Rate = rate;
                this.Timestamp = timestamp;
            }
        }

    }
}
