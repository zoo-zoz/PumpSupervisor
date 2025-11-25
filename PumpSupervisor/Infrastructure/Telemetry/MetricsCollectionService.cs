using InfluxDB.Client;
using InfluxDB.Client.Api.Domain;
using InfluxDB.Client.Writes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace PumpSupervisor.Infrastructure.Telemetry
{
    /// <summary>
    /// 指标收集服务 - 定期收集系统和业务指标并写入 InfluxDB
    /// </summary>
    public class MetricsCollectionService : BackgroundService
    {
        private readonly ILogger<MetricsCollectionService> _logger;
        private readonly IConfiguration _configuration;
        private InfluxDBClient? _client;
        private string _bucket = "";
        private string _org = "";
        private readonly TimeSpan _collectionInterval;
        private readonly TimeSpan _startupDelay; // 新增：启动延迟
        private bool _isEnabled;

        public MetricsCollectionService(
            ILogger<MetricsCollectionService> logger,
            IConfiguration configuration)
        {
            _logger = logger;
            _configuration = configuration;
            _collectionInterval = TimeSpan.FromSeconds(
                configuration.GetValue<int>("OpenTelemetry:MetricsIntervalSeconds", 15));

            // 新增：配置启动延迟，默认 5 秒
            _startupDelay = TimeSpan.FromSeconds(
                configuration.GetValue<int>("OpenTelemetry:MetricsStartupDelaySeconds", 5));
        }

        public override Task StartAsync(CancellationToken cancellationToken)
        {
            try
            {
                var url = _configuration["InfluxDb:Url"] ?? "http://localhost:8086";
                var token = _configuration["InfluxDb:Token"];
                _bucket = _configuration["InfluxDb:Bucket"] ?? "nbcb";
                _org = _configuration["InfluxDb:Org"] ?? "nbcb";

                if (string.IsNullOrEmpty(token))
                {
                    _logger.LogWarning("InfluxDB Token 未配置，指标收集服务将被禁用");
                    _isEnabled = false;
                    return Task.CompletedTask;
                }

                _client = new InfluxDBClient(url, token);
                _isEnabled = true;

                _logger.LogInformation("✓ 指标收集服务启动，采集间隔: {Interval}秒, 启动延迟: {Delay}秒",
                    _collectionInterval.TotalSeconds, _startupDelay.TotalSeconds);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "指标收集服务启动失败");
                _isEnabled = false;
            }

            return base.StartAsync(cancellationToken);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_isEnabled || _client == null)
            {
                _logger.LogInformation("指标收集服务未启用");
                return;
            }

            try
            {
                // ========== 关键修复：等待其他服务启动完成 ==========
                _logger.LogInformation("⏳ 指标收集服务等待系统启动完成... (延迟 {Delay}秒)",
                    _startupDelay.TotalSeconds);
                await Task.Delay(_startupDelay, stoppingToken);
                _logger.LogInformation("✅ 指标收集服务开始运行");
                // ==================================================
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("指标收集服务在启动延迟期间被取消");
                return;
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await CollectAndWriteMetricsAsync(stoppingToken);
                    await Task.Delay(_collectionInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "收集指标时发生错误");

                    // 发生错误后等待较短时间再重试，避免快速失败循环
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }

            _logger.LogInformation("指标收集服务已停止");
        }

        private async Task CollectAndWriteMetricsAsync(CancellationToken cancellationToken)
        {
            var writeApi = _client!.GetWriteApiAsync();
            var points = new List<PointData>();
            var timestamp = DateTime.UtcNow;

            try
            {
                // 1. 进程指标
                var process = Process.GetCurrentProcess();

                points.Add(PointData
                    .Measurement("system_metrics")
                    .Tag("service", "PumpSupervisor")
                    .Tag("metric_type", "memory")
                    .Field("working_set_mb", process.WorkingSet64 / 1024.0 / 1024.0)
                    .Field("private_memory_mb", process.PrivateMemorySize64 / 1024.0 / 1024.0)
                    .Timestamp(timestamp, WritePrecision.Ms));

                // 2. GC 指标
                var gcGen0 = GC.CollectionCount(0);
                var gcGen1 = GC.CollectionCount(1);
                var gcGen2 = GC.CollectionCount(2);
                var gcTotalMemory = GC.GetTotalMemory(false);

                points.Add(PointData
                    .Measurement("system_metrics")
                    .Tag("service", "PumpSupervisor")
                    .Tag("metric_type", "gc")
                    .Field("gen0_collections", gcGen0)
                    .Field("gen1_collections", gcGen1)
                    .Field("gen2_collections", gcGen2)
                    .Field("heap_memory_mb", gcTotalMemory / 1024.0 / 1024.0)
                    .Timestamp(timestamp, WritePrecision.Ms));

                // 3. CPU 指标
                points.Add(PointData
                    .Measurement("system_metrics")
                    .Tag("service", "PumpSupervisor")
                    .Tag("metric_type", "cpu")
                    .Field("processor_time_seconds", process.TotalProcessorTime.TotalSeconds)
                    .Field("user_processor_time_seconds", process.UserProcessorTime.TotalSeconds)
                    .Timestamp(timestamp, WritePrecision.Ms));

                // 4. 线程指标
                points.Add(PointData
                    .Measurement("system_metrics")
                    .Tag("service", "PumpSupervisor")
                    .Tag("metric_type", "threads")
                    .Field("thread_count", process.Threads.Count)
                    .Field("handle_count", process.HandleCount)
                    .Timestamp(timestamp, WritePrecision.Ms));

                // 5. 业务指标 - 添加安全检查
                try
                {
                    var activeConnections = GetActiveConnectionsCount();
                    var slaveInstances = GetSlaveInstancesCount();

                    points.Add(PointData
                        .Measurement("business_metrics")
                        .Tag("service", "PumpSupervisor")
                        .Tag("metric_type", "connections")
                        .Field("active_connections", activeConnections)
                        .Field("slave_instances", slaveInstances)
                        .Timestamp(timestamp, WritePrecision.Ms));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "获取业务指标失败，跳过本次业务指标收集");
                }

                // 写入 InfluxDB
                await writeApi.WritePointsAsync(points, _bucket, _org, cancellationToken);

                _logger.LogDebug("✓ 已写入 {Count} 个指标数据点", points.Count);
            }
            catch (OperationCanceledException)
            {
                throw; // 重新抛出取消异常
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "写入指标到 InfluxDB 失败");
            }
        }

        private static int GetActiveConnectionsCount()
        {
            try
            {
                // 使用反射安全地获取值
                var gauge = AppTelemetry.Metrics.ActiveConnections;
                var method = gauge.GetType().GetMethod("Invoke");
                if (method != null)
                {
                    var result = method.Invoke(gauge, null);
                    return result as int? ?? 0;
                }
                return 0;
            }
            catch
            {
                return 0;
            }
        }

        private static int GetSlaveInstancesCount()
        {
            try
            {
                var gauge = AppTelemetry.Metrics.SlaveInstancesCount;
                var method = gauge.GetType().GetMethod("Invoke");
                if (method != null)
                {
                    var result = method.Invoke(gauge, null);
                    return result as int? ?? 0;
                }
                return 0;
            }
            catch
            {
                return 0;
            }
        }

        public override Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("指标收集服务正在停止...");
            _client?.Dispose();
            return base.StopAsync(cancellationToken);
        }
    }
}