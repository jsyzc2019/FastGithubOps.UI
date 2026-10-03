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
            this.InitFlowChartAsync();
        }

        private async void InitFlowChartAsync()
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
                        this.textBlockRead.Text = "流量读取失败";
                        this.textBlockWrite.Text = ex.GetType().Name;
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
