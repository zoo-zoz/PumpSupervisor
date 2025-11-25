using Microsoft.Extensions.Logging;
using PumpSupervisor.Domain.Events;
using PumpSupervisor.Domain.Models;
using PumpSupervisor.Infrastructure.Cache;
using PumpSupervisor.Infrastructure.Modbus.Commands;
using PumpSupervisor.Infrastructure.Storage.ModbusSlave;
using PumpSupervisor.Infrastructure.Telemetry;
using System.Diagnostics;
using Wolverine;

namespace PumpSupervisor.Infrastructure.Modbus.Handlers
{
    public class ModbusReadCommandHandler
    {
        private readonly IModbusConnectionManager _connectionManager;
        private readonly ILogger<ModbusReadCommandHandler> _logger;
        private readonly ModbusTcpSlaveService _slaveService;
        private readonly IMessageBus _messageBus;
        private readonly IModbusConfigCacheService _configCache; // ✅ 新增

        public ModbusReadCommandHandler(
            IModbusConnectionManager connectionManager,
            ILogger<ModbusReadCommandHandler> logger,
            ModbusTcpSlaveService slaveService,
            IMessageBus messageBus,
            IModbusConfigCacheService configCache) // ✅ 新增
        {
            _connectionManager = connectionManager;
            _logger = logger;
            _slaveService = slaveService;
            _messageBus = messageBus;
            _configCache = configCache; // ✅ 新增
        }

        public async Task<ModbusCommandResult> Handle(ReadModbusDataCommand command, CancellationToken cancellationToken)
        {
            using var activity = AppTelemetry.ActivitySource.StartActivity("ModbusRead", ActivityKind.Client);
            activity?.SetTag("connection.id", command.ConnectionId);
            activity?.SetTag("device.id", command.DeviceId);

            var stopwatch = Stopwatch.StartNew();
            var success = false;
            var registerCount = 0;

            try
            {
                _logger.LogDebug("🔄 开始处理读取: Connection={ConnectionId}, Device={DeviceId}",
                    command.ConnectionId, command.DeviceId);

                // ========== ✅ 修改：使用缓存服务加载配置 ==========
                var config = await _configCache.GetConnectionConfigAsync(command.ConnectionId);
                if (config == null)
                {
                    activity?.SetStatus(ActivityStatusCode.Error, "连接配置未找到");
                    return new ModbusCommandResult(false, $"找不到连接配置: {command.ConnectionId}");
                }

                var deviceConfig = await _configCache.GetDeviceConfigAsync(command.ConnectionId, command.DeviceId);
                if (deviceConfig == null)
                {
                    activity?.SetStatus(ActivityStatusCode.Error, "设备配置未找到");
                    return new ModbusCommandResult(false, $"找不到设备配置: {command.DeviceId}");
                }
                // ===================================================

                activity?.SetTag("register.type", config.RegisterType);
                activity?.SetTag("slave.id", config.SlaveId);

                // 2. 获取连接
                var connection = await _connectionManager.GetConnectionAsync(
                    command.ConnectionId,
                    cancellationToken);

                var mergedData = new Dictionary<int, ushort>();

                // 3. 根据寄存器类型读取数据
                foreach (var block in deviceConfig.ReadBlocks ?? new List<ReadBlock>())
                {
                    try
                    {
                        var registerType = config.RegisterType.ToLower();

                        _logger.LogDebug("准备读取 Block: Type={RegisterType}, Start={StartAddress}, Count={Count}",
                            registerType, block.Start, block.Count);

                        ushort[] blockData;

                        switch (registerType)
                        {
                            case "holding":
                                blockData = await connection.ReadHoldingRegistersAsync(
                                    (byte)config.SlaveId,
                                    (ushort)block.Start,
                                    (ushort)block.Count,
                                    cancellationToken);
                                break;

                            case "input":
                                blockData = await connection.ReadInputRegistersAsync(
                                    (byte)config.SlaveId,
                                    (ushort)block.Start,
                                    (ushort)block.Count,
                                    cancellationToken);
                                break;

                            case "coil":
                                var coils = await connection.ReadCoilsAsync(
                                    (byte)config.SlaveId,
                                    (ushort)block.Start,
                                    (ushort)block.Count,
                                    cancellationToken);
                                blockData = coils.Select(b => (ushort)(b ? 1 : 0)).ToArray();
                                break;

                            case "discrete_input":
                                var discreteInputs = await connection.ReadInputsAsync(
                                    (byte)config.SlaveId,
                                    (ushort)block.Start,
                                    (ushort)block.Count,
                                    cancellationToken);
                                blockData = discreteInputs.Select(b => (ushort)(b ? 1 : 0)).ToArray();
                                break;

                            default:
                                _logger.LogError("❌ 不支持的寄存器类型: {RegisterType}", config.RegisterType);
                                throw new ArgumentException($"不支持的寄存器类型: {config.RegisterType}");
                        }

                        _logger.LogDebug(
                            "读取原始数据 - Connection={ConnectionId}, Type={RegisterType}, Start={StartAddress}, Count={Count}, Values=[{Values}]",
                            command.ConnectionId,
                            registerType,
                            block.Start,
                            block.Count,
                            string.Join(", ", blockData.Select(r => $"0x{r:X4}"))
                        );

                        // 合并数据到字典
                        for (int i = 0; i < blockData.Length; i++)
                        {
                            mergedData[block.Start + i] = blockData[i];
                        }

                        _logger.LogDebug("✓ 读取 Block: Start={StartAddress}, Count={Count}, Actual={ActualCount}",
                            block.Start, block.Count, blockData.Length);

                        // 4. 写入虚拟 Slave
                        if (_slaveService.HasSlaveInstance(command.ConnectionId))
                        {
                            try
                            {
                                switch (registerType)
                                {
                                    case "input":
                                        await _slaveService.WriteInputRegistersAsync(
                                            command.ConnectionId,
                                            (ushort)block.Start,
                                            blockData);
                                        break;

                                    case "holding":
                                        await _slaveService.WriteRegistersAsync(
                                            command.ConnectionId,
                                            (ushort)block.Start,
                                            blockData);
                                        break;

                                    case "coil":
                                        var coilBools = blockData.Select(v => v != 0).ToArray();
                                        await _slaveService.WriteCoilsAsync(
                                            command.ConnectionId,
                                            (ushort)block.Start,
                                            coilBools);
                                        break;

                                    case "discrete_input":
                                        var discreteBools = blockData.Select(v => v != 0).ToArray();
                                        await _slaveService.WriteDiscreteInputsAsync(
                                            command.ConnectionId,
                                            (ushort)block.Start,
                                            discreteBools);
                                        break;
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex,
                                    "❌ 同步数据到Slave失败: Connection={ConnectionId}, Start={StartAddress}",
                                    command.ConnectionId, block.Start);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "❌ 读取 Block 失败: Start={StartAddress}, Count={Count}",
                            block.Start, block.Count);
                        activity?.AddException(ex);
                    }
                }

                if (mergedData.Count == 0)
                {
                    _logger.LogWarning("⚠️ 没有读取到数据: {DeviceId}", command.DeviceId);
                    activity?.SetStatus(ActivityStatusCode.Error, "没有读取到数据");
                    return new ModbusCommandResult(false, "没有读取到数据");
                }

                stopwatch.Stop();
                registerCount = mergedData.Count;
                success = true;

                // 记录指标
                AppTelemetry.Metrics.ModbusReadCounter.Add(1,
                    new KeyValuePair<string, object?>("connection.id", command.ConnectionId),
                    new KeyValuePair<string, object?>("device.id", command.DeviceId),
                    new KeyValuePair<string, object?>("register.type", config.RegisterType));

                AppTelemetry.Metrics.ModbusReadDuration.Record(stopwatch.Elapsed.TotalMilliseconds,
                    new KeyValuePair<string, object?>("connection.id", command.ConnectionId),
                    new KeyValuePair<string, object?>("device.id", command.DeviceId));

                activity?.SetTag("register.count", registerCount);
                activity?.SetTag("duration.ms", stopwatch.Elapsed.TotalMilliseconds);
                activity?.SetStatus(ActivityStatusCode.Ok);

                _logger.LogInformation(
                    "✅ Modbus读取成功: {ConnectionId}/{DeviceId}, 耗时: {DurationMs}ms, 寄存器数: {RegisterCount}",
                    command.ConnectionId,
                    command.DeviceId,
                    stopwatch.Elapsed.TotalMilliseconds,
                    registerCount);

                // 5. 发布数据采集事件
                var collectedEvent = new ModbusDataCollectedEvent(
                    command.ConnectionId,
                    command.DeviceId,
                    mergedData,
                    DateTime.Now
                );

                await _messageBus.PublishAsync(collectedEvent);

                return new ModbusCommandResult(true, "读取成功", mergedData);
            }
            catch (Exception ex)
            {
                stopwatch.Stop();

                // 记录错误指标
                AppTelemetry.Metrics.ModbusReadErrorCounter.Add(1,
                    new KeyValuePair<string, object?>("connection.id", command.ConnectionId),
                    new KeyValuePair<string, object?>("device.id", command.DeviceId),
                    new KeyValuePair<string, object?>("error.type", ex.GetType().Name));

                activity?.AddException(ex);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);

                _logger.LogError(ex,
                    "❌ Modbus读取失败: {ConnectionId}/{DeviceId}, 耗时: {DurationMs}ms, 错误: {ErrorMessage}",
                    command.ConnectionId,
                    command.DeviceId,
                    stopwatch.Elapsed.TotalMilliseconds,
                    ex.Message);

                return new ModbusCommandResult(false, $"读取失败: {ex.Message}");
            }
        }
    }
}