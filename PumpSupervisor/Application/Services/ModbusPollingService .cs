using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PumpSupervisor.Domain.Models;
using PumpSupervisor.Infrastructure.Modbus;
using PumpSupervisor.Infrastructure.Modbus.Commands;
using PumpSupervisor.Infrastructure.Storage.ModbusSlave;
using System.Collections.Concurrent;
using System.Text.Json;
using Wolverine;

namespace PumpSupervisor.Application.Services
{
    public class ModbusPollingService : BackgroundService
    {
        private readonly ILogger<ModbusPollingService> _logger;
        private readonly IMessageBus _messageBus;
        private readonly IConfiguration _configuration;
        private readonly IModbusConnectionManager _connectionManager;
        private readonly ConcurrentDictionary<string, Timer> _timers = new();
        private readonly ConcurrentDictionary<string, Task> _continuousTasks = new();
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _continuousCts = new();
        private List<ModbusConnectionConfig> _connectionConfigs = new();
        private readonly ModbusTcpSlaveService _slaveService;

        public ModbusPollingService(
            ILogger<ModbusPollingService> logger,
            IMessageBus messageBus,
            IConfiguration configuration,
            IModbusConnectionManager connectionManager,
            ModbusTcpSlaveService slaveService)
        {
            _logger = logger;
            _messageBus = messageBus;
            _configuration = configuration;
            _connectionManager = connectionManager;
            _slaveService = slaveService;
        }

        public override async Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("ModbusPollingService 正在启动...");

            await LoadConfigurationAsync();
            _logger.LogInformation("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
            _logger.LogInformation("注册设备连接列表:");

            foreach (var connectionConfig in _connectionConfigs.Where(c => c.Enabled))
            {
                _connectionManager.RegisterConfiguration(connectionConfig);
                _logger.LogInformation("已注册连接配置: {ConnectionId} - 设备数: {DeviceCount}",
                    connectionConfig.Id,
                    connectionConfig.Devices.Count(d => d.Enabled));
            }
            _logger.LogInformation("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
            await base.StartAsync(cancellationToken);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("ModbusPollingService 开始执行");

            try
            {
                _logger.LogInformation("⏳等待 ModbusTcpSlaveService 初始化完成...");
                var timeout = TimeSpan.FromSeconds(30);
                var cts = new CancellationTokenSource(timeout);
                var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, cts.Token);

                try
                {
                    await _slaveService.WaitForInitializationAsync(combinedCts.Token);
                    _logger.LogInformation("✅ ModbusTcpSlaveService 初始化完成");

                    var slaveInfos = _slaveService.GetAllSlaveInfo();
                    if (slaveInfos.Count > 0)
                    {
                        _logger.LogInformation("📊 可用的 Slave 实例 ({Count}):", slaveInfos.Count);
                        foreach (var info in slaveInfos)
                        {
                            _logger.LogInformation("  - {ConnectionId} @ {Address}",
                                info["ConnectionId"], info["Address"]);
                        }
                    }
                    else
                    {
                        _logger.LogWarning("⚠️ 没有可用的 Slave 实例（可能没有配置 slave_port）");
                    }
                }
                catch (OperationCanceledException)
                {
                    _logger.LogError("❌ 等待 Slave 服务超时 ({Timeout}秒)", timeout.TotalSeconds);
                    _logger.LogWarning("⚠️ 继续启动轮询服务，但虚拟 Slave 功能将不可用");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ 等待 Slave 服务失败");
            }

            _logger.LogInformation("开始预建立连接...");

            var connectionTasks = _connectionConfigs
                .Where(c => c.Enabled)
                .Select(async c =>
                {
                    try
                    {
                        await _connectionManager.EnsureConnectedAsync(c.Id, stoppingToken);
                        _logger.LogInformation("✓ 连接 {ConnectionId} 已就绪", c.Id);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "✗ 连接 {ConnectionId} 初始化失败", c.Id);
                    }
                });

            await Task.WhenAll(connectionTasks);

            _logger.LogInformation("开始启动轮询任务...");

            int periodicDeviceCount = 0;
            int continuousDeviceCount = 0;
            int onDemandDeviceCount = 0;

            foreach (var connection in _connectionConfigs.Where(c => c.Enabled))
            {
                foreach (var device in connection.Devices.Where(d => d.Enabled))
                {
                    switch (device.PollMode?.ToLower())
                    {
                        case "continuous":
                            StartContinuousPolling(connection, device, stoppingToken);
                            continuousDeviceCount++;
                            break;

                        case "periodic":
                            StartPeriodicPolling(connection, device, stoppingToken);
                            periodicDeviceCount++;
                            break;

                        case "on-demand":
                        default:
                            _logger.LogInformation("✓ 设备: {ConnectionId}/{DeviceId} 配置为按需轮询，跳过自动轮询",
                                connection.Id, device.Id);
                            onDemandDeviceCount++;
                            break;
                    }
                }
            }

            _logger.LogInformation("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
            _logger.LogInformation(
                "轮询任务启动完成 - 连续轮询: {ContinuousCount}, 周期轮询: {PeriodicCount}, 按需轮询: {OnDemandCount}",
                continuousDeviceCount, periodicDeviceCount, onDemandDeviceCount);
            _logger.LogInformation("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");

            try
            {
                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (TaskCanceledException)
            {
                _logger.LogInformation("ModbusPollingService 收到停止信号");
            }
        }

        private void StartContinuousPolling(
            ModbusConnectionConfig connection,
            DeviceConfig device,
            CancellationToken stoppingToken)
        {
            var key = $"{connection.Id}:{device.Id}";
            var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            _continuousCts[key] = cts;

            // 获取最小轮询间隔，默认10ms
            var minPollInterval = connection.MinPollInterval ?? 10;

            var task = Task.Run(async () =>
            {
                _logger.LogInformation("🔄 启动连续采集: {Key}, 最小间隔: {MinInterval}ms",
                    key, minPollInterval);

                var consecutiveErrors = 0;
                var maxConsecutiveErrors = 10;

                while (!cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        var command = new ReadModbusDataCommand(
                            connection.Id,
                            device.Id,
                            Priority: 1
                        );

                        var result = await _messageBus.InvokeAsync<ModbusCommandResult>(
                            command,
                            cts.Token,
                            timeout: TimeSpan.FromSeconds(10));

                        if (result?.Success == true)
                        {
                            var registerData = result.GetRegisterData();
                            _logger.LogTrace("连续采集成功: {Key}, 寄存器数: {Count}",
                                key, registerData?.Count ?? 0);

                            // 重置错误计数
                            consecutiveErrors = 0;
                        }
                        else
                        {
                            consecutiveErrors++;
                            _logger.LogWarning("连续采集失败 ({ErrorCount}/{MaxErrors}): {Key}, 原因: {Message}",
                                consecutiveErrors, maxConsecutiveErrors, key, result?.Message ?? "未知");

                            // 如果连续失败太多次，增加延迟
                            if (consecutiveErrors >= maxConsecutiveErrors)
                            {
                                _logger.LogError("连续采集失败次数过多，等待5秒后重试: {Key}", key);
                                await Task.Delay(5000, cts.Token);
                                consecutiveErrors = 0;
                            }
                        }

                        // 最小间隔延迟
                        if (minPollInterval > 0)
                        {
                            await Task.Delay(minPollInterval, cts.Token);
                        }
                        // 如果设置为0，则立即进行下一次采集（真正的连续模式）
                    }
                    catch (OperationCanceledException)
                    {
                        _logger.LogDebug("连续采集被取消: {Key}", key);
                        break;
                    }
                    catch (Exception ex)
                    {
                        consecutiveErrors++;
                        _logger.LogError(ex, "连续采集异常 ({ErrorCount}/{MaxErrors}): {Key}",
                            consecutiveErrors, maxConsecutiveErrors, key);

                        if (consecutiveErrors >= maxConsecutiveErrors)
                        {
                            _logger.LogError("连续采集异常次数过多，等待5秒后重试: {Key}", key);
                            await Task.Delay(5000, cts.Token);
                            consecutiveErrors = 0;
                        }
                        else
                        {
                            await Task.Delay(1000, cts.Token);
                        }
                    }
                }

                _logger.LogInformation("⏹️ 停止连续采集: {Key}", key);
            }, cts.Token);

            _continuousTasks[key] = task;

            _logger.LogInformation("✓ 设备: {ConnectionId}/{DeviceId} 已启动连续采集模式, 最小间隔: {MinInterval}ms",
                connection.Id, device.Id, minPollInterval);
        }

        private void StartPeriodicPolling(
            ModbusConnectionConfig connection,
            DeviceConfig device,
            CancellationToken stoppingToken)
        {
            var interval = ParseDuration(connection.PollInterval);
            var key = $"{connection.Id}:{device.Id}";

            _logger.LogDebug("准备启动定时器: {Key}, 间隔={Interval}ms", key, interval);

            var timer = new Timer(async _ =>
            {
                if (stoppingToken.IsCancellationRequested)
                {
                    _logger.LogDebug("定时器 {Key} 检测到取消信号", key);
                    return;
                }

                try
                {
                    _logger.LogTrace("定时器触发: {Key}", key);
                    var command = new ReadModbusDataCommand(
                        connection.Id,
                        device.Id,
                        Priority: 1
                    );

                    _logger.LogTrace("发送读取命令: {ConnectionId}/{DeviceId}",
                        connection.Id, device.Id);

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var result = await _messageBus.InvokeAsync<ModbusCommandResult>(
                                command,
                                stoppingToken,
                                timeout: TimeSpan.FromSeconds(10));

                            if (result?.Success == true)
                            {
                                var registerData = result.GetRegisterData();
                                _logger.LogTrace("读取成功: {ConnectionId}/{DeviceId}, 寄存器数: {Count}",
                                    connection.Id, device.Id, registerData?.Count ?? 0);
                            }
                            else
                            {
                                _logger.LogWarning("读取失败: {ConnectionId}/{DeviceId}, 原因: {Message}",
                                    connection.Id, device.Id, result?.Message ?? "未知");
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "执行读取命令异常: {ConnectionId}/{DeviceId}",
                                connection.Id, device.Id);
                        }
                    }, stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "定时器回调异常: {ConnectionId}/{DeviceId}",
                        connection.Id, device.Id);
                }
            }, null, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(interval));

            _timers[key] = timer;

            _logger.LogInformation("✓ 设备: {ConnectionId}/{DeviceId} 配置为定时轮询,已启动定时器 间隔: {Interval}ms",
                connection.Id, device.Id, interval);
        }

        private async Task LoadConfigurationAsync()
        {
            try
            {
                var configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "readModbus.json");

                if (!File.Exists(configPath))
                {
                    _logger.LogError("配置文件不存在: {Path}", configPath);
                    throw new FileNotFoundException($"配置文件不存在: {configPath}");
                }

                var json = await File.ReadAllTextAsync(configPath);

                var config = JsonSerializer.Deserialize<ModbusConfig>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                _connectionConfigs = config?.Connections ?? new List<ModbusConnectionConfig>();

                _logger.LogInformation("加载配置成功，连接数: {Count}", _connectionConfigs.Count);

                foreach (var conn in _connectionConfigs.Where(c => c.Enabled))
                {
                    _logger.LogDebug("连接 {ConnectionId}: Type={Type}, SlaveId={SlaveId}, PollInterval={PollInterval}, MinPollInterval={MinInterval}ms",
                        conn.Id, conn.Type, conn.SlaveId, conn.PollInterval, conn.MinPollInterval ?? 10);

                    foreach (var device in conn.Devices.Where(d => d.Enabled))
                    {
                        _logger.LogDebug("  设备 {DeviceId}: PollMode={PollMode}, ReadBlocks={BlockCount}",
                            device.Id, device.PollMode ?? "periodic", device.ReadBlocks.Count);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "加载Modbus配置失败");
                throw;
            }
        }

        private int ParseDuration(string duration)
        {
            if (string.IsNullOrEmpty(duration)) return 1000;

            var numericPart = duration.TrimEnd('m', 's');
            if (!int.TryParse(numericPart, out var value))
            {
                _logger.LogWarning("无法解析持续时间: {Duration}, 使用默认值 1000ms", duration);
                return 1000;
            }

            return duration.EndsWith("ms") ? value : value * 1000;
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("ModbusPollingService 正在停止...");

            // 停止所有连续采集任务
            foreach (var kvp in _continuousCts)
            {
                try
                {
                    kvp.Value.Cancel();
                    _logger.LogDebug("已取消连续采集: {Key}", kvp.Key);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "取消连续采集失败: {Key}", kvp.Key);
                }
            }

            // 等待所有连续采集任务完成
            try
            {
                await Task.WhenAll(_continuousTasks.Values);
                _logger.LogInformation("所有连续采集任务已停止");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "等待连续采集任务完成时出错");
            }

            // 清理资源
            foreach (var cts in _continuousCts.Values)
            {
                cts?.Dispose();
            }
            _continuousCts.Clear();
            _continuousTasks.Clear();

            // 停止所有定时器
            foreach (var kvp in _timers)
            {
                try
                {
                    await kvp.Value.DisposeAsync();
                    _logger.LogDebug("已停止定时器: {Key}", kvp.Key);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "停止定时器失败: {Key}", kvp.Key);
                }
            }
            _timers.Clear();

            _logger.LogInformation("已停止所有轮询定时器和连续采集任务");

            await base.StopAsync(cancellationToken);
        }
    }
}