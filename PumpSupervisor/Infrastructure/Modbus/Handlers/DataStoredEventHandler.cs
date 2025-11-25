using Microsoft.Extensions.Logging;
using PumpSupervisor.Domain.Events;
using PumpSupervisor.Infrastructure.Messaging.Mqtt;
using PumpSupervisor.Infrastructure.Telemetry;
using System.Diagnostics;

namespace PumpSupervisor.Infrastructure.Modbus.Handlers
{
    public class DataStoredEventHandler
    {
        private readonly IMqttPublisher _mqttPublisher;
        private readonly ILogger<DataStoredEventHandler> _logger;

        public DataStoredEventHandler(
            IMqttPublisher mqttPublisher,
            ILogger<DataStoredEventHandler> logger)
        {
            _mqttPublisher = mqttPublisher;
            _logger = logger;
        }

        public async Task Handle(DataStoredEvent @event, CancellationToken cancellationToken)
        {
            using var activity = AppTelemetry.ActivitySource.StartActivity("DataStoredEvent", ActivityKind.Consumer);
            activity?.SetTag("connection.id", @event.ConnectionId);
            activity?.SetTag("device.id", @event.DeviceId);
            activity?.SetTag("data.points.count", @event.DataPointCount); var stopwatch = Stopwatch.StartNew(); try
            {
                _logger.LogDebug("🔄 开始处理数据存储事件: {ConnectionId}/{DeviceId}",
                    @event.ConnectionId, @event.DeviceId); if (@event.DataBatch != null)
                {
                    await _mqttPublisher.PublishDataBatchAsync(@event.DataBatch, cancellationToken); stopwatch.Stop();                // 记录指标
                    AppTelemetry.Metrics.MqttPublishCounter.Add(1,
                        new KeyValuePair<string, object?>("connection.id", @event.ConnectionId),
                        new KeyValuePair<string, object?>("device.id", @event.DeviceId)); activity?.SetTag("duration.ms", stopwatch.Elapsed.TotalMilliseconds);
                    activity?.SetStatus(ActivityStatusCode.Ok); _logger.LogInformation(
                        "✅ 数据已发布到MQTT: {ConnectionId}/{DeviceId}, 数据点数: {DataPointCount}",
                        @event.ConnectionId,
                        @event.DeviceId,
                        @event.DataBatch.DataPoints.Count);
                }
                else
                {
                    activity?.SetStatus(ActivityStatusCode.Error, "DataBatch为空");
                    _logger.LogWarning("⚠️ DataStoredEvent 中没有 DataBatch: {ConnectionId}/{DeviceId}",
                        @event.ConnectionId, @event.DeviceId);
                }
            }
            catch (Exception ex)
            {
                stopwatch.Stop();            // 记录错误指标
                AppTelemetry.Metrics.MqttPublishErrorCounter.Add(1,
                    new KeyValuePair<string, object?>("connection.id", @event.ConnectionId),
                    new KeyValuePair<string, object?>("device.id", @event.DeviceId),
                    new KeyValuePair<string, object?>("error.type", ex.GetType().Name));
                activity?.AddException(ex);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message); _logger.LogError(ex,
                    "❌ MQTT发布失败: {ConnectionId}/{DeviceId}, 错误: {ErrorMessage}",
                    @event.ConnectionId,
                    @event.DeviceId,
                    ex.Message);
            }
        }
    }// 值变化事件处理器

    public class ParameterValueChangedEventHandler
    {
        private readonly IMqttPublisher _mqttPublisher;

        private readonly ILogger<ParameterValueChangedEventHandler> _logger; public ParameterValueChangedEventHandler(
            IMqttPublisher mqttPublisher,
            ILogger<ParameterValueChangedEventHandler> logger)
        {
            _mqttPublisher = mqttPublisher;
            _logger = logger;
        }

        public async Task Handle(ParameterValueChangedEvent @event, CancellationToken cancellationToken)
        {
            using var activity = AppTelemetry.ActivitySource.StartActivity("ParameterValueChanged", ActivityKind.Consumer);
            activity?.SetTag("connection.id", @event.ConnectionId);
            activity?.SetTag("device.id", @event.DeviceId);
            activity?.SetTag("parameter.code", @event.ParameterCode); try
            {
                await _mqttPublisher.PublishValueChangeAsync(@event, cancellationToken); activity?.SetStatus(ActivityStatusCode.Ok); _logger.LogInformation(
                    "✅ 参数值变化已发布: {ConnectionId}/{DeviceId}/{ParamCode}, {OldValue} → {NewValue}",
                    @event.ConnectionId,
                    @event.DeviceId,
                    @event.ParameterCode,
                    @event.OldValue,
                    @event.NewValue);
            }
            catch (Exception ex)
            {
                activity?.AddException(ex);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message); _logger.LogError(ex,
                    "❌ 发布参数值变化失败: {DeviceId}/{ParamCode}",
                    @event.DeviceId,
                    @event.ParameterCode);
            }
        }
    }
}