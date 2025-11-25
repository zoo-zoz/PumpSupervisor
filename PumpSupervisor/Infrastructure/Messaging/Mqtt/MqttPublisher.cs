using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using MQTTnet;
using PumpSupervisor.Domain.Events;

using PumpSupervisor.Domain.Models;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace PumpSupervisor.Infrastructure.Messaging.Mqtt
{
    public interface IMqttPublisher
    {
        Task PublishDataBatchAsync(ModbusDataBatch batch, CancellationToken cancellationToken = default);

        Task PublishValueChangeAsync(ParameterValueChangedEvent @event, CancellationToken cancellationToken = default);

        bool IsConnected { get; }
    }

    public class MqttPublisher : IMqttPublisher, IHostedService, IAsyncDisposable
    {
        private readonly ILogger<MqttPublisher> _logger;
        private readonly IConfiguration _configuration;
        private IMqttClient? _mqttClient;
        private MqttClientOptions? _options;
        private readonly string _baseTopic;
        private bool _isConnected;
        private readonly SemaphoreSlim _connectionLock = new(1, 1);
        private Timer? _reconnectTimer;

        // 添加 JSON 序列化选项,确保中文不被转义
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        public bool IsConnected => _isConnected && (_mqttClient?.IsConnected ?? false);

        public MqttPublisher(
            ILogger<MqttPublisher> logger,
            IConfiguration configuration)
        {
            _logger = logger;
            _configuration = configuration;
            _baseTopic = configuration["Mqtt:BaseTopic"] ?? "pump/data";
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("MQTT Publisher 正在启动...");

            try
            {
                var broker = _configuration["Mqtt:Broker"] ?? "localhost";
                var port = _configuration.GetValue<int>("Mqtt:Port", 1883);
                var clientId = _configuration["Mqtt:ClientId"] ?? $"PumpSupervisor_{Guid.NewGuid():N}";
                var username = _configuration["Mqtt:Username"];
                var password = _configuration["Mqtt:Password"];

                _logger.LogInformation("MQTT 配置: Broker={Broker}, Port={Port}, ClientId={ClientId}",
                    broker, port, clientId);

                var factory = new MqttClientFactory();
                _mqttClient = factory.CreateMqttClient();

                var optionsBuilder = new MqttClientOptionsBuilder()
                    .WithTcpServer(broker, port)
                    .WithClientId(clientId)
                    .WithCleanSession()
                    .WithKeepAlivePeriod(TimeSpan.FromSeconds(60));

                if (!string.IsNullOrEmpty(username))
                {
                    optionsBuilder.WithCredentials(username, password);
                }

                _options = optionsBuilder.Build();

                // 注册事件处理器
                _mqttClient.ConnectedAsync += OnConnectedAsync;
                _mqttClient.DisconnectedAsync += OnDisconnectedAsync;

                // 同步等待首次连接
                await ConnectAsync(cancellationToken);

                // 启动重连定时器
                _reconnectTimer = new Timer(async _ => await EnsureConnectedAsync(), null,
                    TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30));

                _logger.LogInformation("✓ MQTT Publisher 启动完成");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "✗ MQTT Publisher 启动失败,将在后台重试连接");
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("MQTT Publisher 正在停止...");

            _reconnectTimer?.Dispose();

            if (_mqttClient?.IsConnected == true)
            {
                await _mqttClient.DisconnectAsync(cancellationToken: cancellationToken);
            }

            _logger.LogInformation("MQTT Publisher 已停止");
        }

        private async Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            await _connectionLock.WaitAsync(cancellationToken);
            try
            {
                if (_mqttClient?.IsConnected == true)
                {
                    _logger.LogDebug("MQTT 已连接,跳过重复连接");
                    return;
                }

                _logger.LogInformation("正在连接 MQTT Broker...");

                var result = await _mqttClient!.ConnectAsync(_options!, cancellationToken);

                if (result.ResultCode == MqttClientConnectResultCode.Success)
                {
                    _logger.LogInformation("✓ MQTT 连接成功");
                }
                else
                {
                    _logger.LogWarning("✗ MQTT 连接失败: {ResultCode}", result.ResultCode);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MQTT 连接异常");
            }
            finally
            {
                _connectionLock.Release();
            }
        }

        private async Task EnsureConnectedAsync()
        {
            if (_mqttClient?.IsConnected != true)
            {
                _logger.LogDebug("MQTT 未连接,尝试重连...");
                await ConnectAsync();
            }
        }

        private async Task OnConnectedAsync(MqttClientConnectedEventArgs e)
        {
            _isConnected = true;
            _logger.LogInformation("✓ MQTT 连接已建立");
            await Task.CompletedTask;
        }

        private async Task OnDisconnectedAsync(MqttClientDisconnectedEventArgs e)
        {
            _isConnected = false;
            _logger.LogWarning("MQTT 连接断开,原因: {Reason}", e.Reason);
            await Task.CompletedTask;
        }

        public async Task PublishDataBatchAsync(
            ModbusDataBatch batch,
            CancellationToken cancellationToken = default)
        {
            if (!IsConnected)
            {
                _logger.LogWarning("MQTT 未连接,跳过发布数据批次: {DeviceId}", batch.DeviceId);
                return;
            }

            try
            {
                var topic = $"{_baseTopic}/{batch.ConnectionId}/{batch.DeviceId}/data";
                var payload = new
                {
                    connection_id = batch.ConnectionId,
                    device_id = batch.DeviceId,
                    timestamp = batch.Timestamp,
                    data_points = batch.DataPoints.Select(dp => new
                    {
                        parameter_code = dp.ParameterCode,
                        parameter_name = dp.ParameterName,
                        value = dp.ParsedValue,
                        unit = dp.Unit
                    })
                };

                var json = JsonSerializer.Serialize(payload, JsonOptions);
                var payloadBytes = Encoding.UTF8.GetBytes(json);

                var message = new MqttApplicationMessageBuilder()
                    .WithTopic(topic)
                    .WithPayload(payloadBytes)
                    .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                    .WithRetainFlag(false)
                    .Build();

                await _mqttClient!.PublishAsync(message, cancellationToken);

                _logger.LogDebug("✓ MQTT 发布成功: {Topic}, 数据点数: {Count}",
                    topic, batch.DataPoints.Count);

                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug("发送的 JSON 内容: {Json}", json);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MQTT 发布失败: {ConnectionId}/{DeviceId}",
                    batch.ConnectionId, batch.DeviceId);
            }
        }

        public async Task PublishValueChangeAsync(
            ParameterValueChangedEvent @event,
            CancellationToken cancellationToken = default)
        {
            if (!IsConnected)
            {
                _logger.LogWarning("MQTT 未连接,跳过发布值变化事件");
                return;
            }

            try
            {
                var topic = $"{_baseTopic}/{@event.ConnectionId}/{@event.DeviceId}/changes";

                var payload = new
                {
                    connection_id = @event.ConnectionId,
                    device_id = @event.DeviceId,
                    parameter_code = @event.ParameterCode,
                    parameter_name = @event.ParameterName,
                    old_value = @event.OldValue,
                    new_value = @event.NewValue,
                    timestamp = @event.Timestamp
                };

                var json = JsonSerializer.Serialize(payload, JsonOptions);
                var payloadBytes = Encoding.UTF8.GetBytes(json);

                var message = new MqttApplicationMessageBuilder()
                    .WithTopic(topic)
                    .WithPayload(payloadBytes)
                    .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                    .WithRetainFlag(false)
                    .Build();

                await _mqttClient!.PublishAsync(message, cancellationToken);

                _logger.LogInformation("✓ MQTT 发布值变化: {Topic}, {ParamCode}: {OldValue} -> {NewValue}",
                    topic, @event.ParameterCode, @event.OldValue, @event.NewValue);

                // 调试日志
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug("发送的 JSON 内容: {Json}", json);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MQTT 发布值变化事件失败");
            }
        }

        public async ValueTask DisposeAsync()
        {
            _reconnectTimer?.Dispose();

            if (_mqttClient?.IsConnected == true)
            {
                await _mqttClient.DisconnectAsync();
            }
            _mqttClient?.Dispose();
            _connectionLock?.Dispose();
        }
    }
}