using Microsoft.Extensions.Logging;
using PumpSupervisor.Domain.Events;
using PumpSupervisor.Infrastructure.Storage.InfluxDb;
using PumpSupervisor.Infrastructure.Telemetry;
using System.Diagnostics;
using Wolverine;

namespace PumpSupervisor.Infrastructure.Modbus.Handlers
{
    public class DataParsedEventHandler
    {
        private readonly IInfluxDbService _influxDbService;
        private readonly ILogger<DataParsedEventHandler> _logger;
        private readonly IMessageBus _messageBus;

        public DataParsedEventHandler(
            IInfluxDbService influxDbService,
            ILogger<DataParsedEventHandler> logger,
            IMessageBus messageBus)
        {
            _influxDbService = influxDbService;
            _logger = logger;
            _messageBus = messageBus;
        }

        public async Task Handle(DataParsedEvent @event, CancellationToken cancellationToken)
        {
            using var activity = AppTelemetry.ActivitySource.StartActivity("DataParsedEvent", ActivityKind.Consumer);
            activity?.SetTag("connection.id", @event.DataBatch.ConnectionId);
            activity?.SetTag("device.id", @event.DataBatch.DeviceId);
            activity?.SetTag("data.points.count", @event.DataBatch.DataPoints.Count);

            var stopwatch = Stopwatch.StartNew();

            try
            {
                _logger.LogDebug("🔄 开始处理数据解析事件: {ConnectionId}/{DeviceId}",
                    @event.DataBatch.ConnectionId, @event.DataBatch.DeviceId);

                // 写入 InfluxDB
                await _influxDbService.WriteDataBatchAsync(@event.DataBatch, cancellationToken);

                stopwatch.Stop();

                // 记录指标
                AppTelemetry.Metrics.InfluxDbWriteCounter.Add(1,
                    new KeyValuePair<string, object?>("connection.id", @event.DataBatch.ConnectionId),
                    new KeyValuePair<string, object?>("device.id", @event.DataBatch.DeviceId));

                AppTelemetry.Metrics.InfluxDbWriteDuration.Record(stopwatch.Elapsed.TotalMilliseconds,
                    new KeyValuePair<string, object?>("connection.id", @event.DataBatch.ConnectionId),
                    new KeyValuePair<string, object?>("device.id", @event.DataBatch.DeviceId));

                AppTelemetry.Metrics.DataPointsProcessed.Add(@event.DataBatch.DataPoints.Count,
                    new KeyValuePair<string, object?>("connection.id", @event.DataBatch.ConnectionId),
                    new KeyValuePair<string, object?>("device.id", @event.DataBatch.DeviceId));

                activity?.SetTag("duration.ms", stopwatch.Elapsed.TotalMilliseconds);
                activity?.SetStatus(ActivityStatusCode.Ok);

                _logger.LogInformation(
                    "✅ 数据已写入InfluxDB: {ConnectionId}/{DeviceId}, 数据点数: {DataPointCount}",
                    @event.DataBatch.ConnectionId,
                    @event.DataBatch.DeviceId,
                    @event.DataBatch.DataPoints.Count);

                await _messageBus.PublishAsync(new DataStoredEvent(
                    @event.DataBatch.ConnectionId,
                    @event.DataBatch.DeviceId,
                    @event.DataBatch.DataPoints.Count,
                    DateTime.Now,
                    @event.DataBatch
                ));
            }
            catch (Exception ex)
            {
                stopwatch.Stop();

                // 记录错误指标
                AppTelemetry.Metrics.InfluxDbWriteErrorCounter.Add(1,
                    new KeyValuePair<string, object?>("connection.id", @event.DataBatch.ConnectionId),
                    new KeyValuePair<string, object?>("device.id", @event.DataBatch.DeviceId),
                    new KeyValuePair<string, object?>("error.type", ex.GetType().Name));

                activity?.AddException(ex);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);

                _logger.LogError(ex,
                    "❌ 写入InfluxDB失败: {ConnectionId}/{DeviceId}, 错误: {ErrorMessage}",
                    @event.DataBatch.ConnectionId,
                    @event.DataBatch.DeviceId,
                    ex.Message);
            }
        }
    }
}