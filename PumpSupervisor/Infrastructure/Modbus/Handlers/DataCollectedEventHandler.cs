using Microsoft.Extensions.Logging;
using PumpSupervisor.Application.Services;
using PumpSupervisor.Domain.Events;
using PumpSupervisor.Domain.Models;
using PumpSupervisor.Infrastructure.Cache;
using PumpSupervisor.Infrastructure.Modbus.DataParser;
using PumpSupervisor.Infrastructure.Telemetry;
using System.Diagnostics;
using Wolverine;

namespace PumpSupervisor.Infrastructure.Modbus.Handlers
{
    public class DataCollectedEventHandler
    {
        private readonly IModbusDataParser _dataParser;
        private readonly ILogger<DataCollectedEventHandler> _logger;
        private readonly IDataBatchCacheService _cacheService;
        private readonly IModbusConfigCacheService _configCache;
        private readonly IMessageBus _messageBus;
        private readonly IParameterValueTracker _valueTracker;

        public DataCollectedEventHandler(
            IModbusDataParser dataParser,
            ILogger<DataCollectedEventHandler> logger,
            IDataBatchCacheService cacheService,
            IModbusConfigCacheService configCache,
            IMessageBus messageBus,
            IParameterValueTracker valueTracker)
        {
            _dataParser = dataParser;
            _logger = logger;
            _cacheService = cacheService;
            _configCache = configCache;
            _messageBus = messageBus;
            _valueTracker = valueTracker;
        }

        public async Task<DataParsedEvent?> Handle(ModbusDataCollectedEvent @event, CancellationToken cancellationToken)
        {
            using var activity = AppTelemetry.ActivitySource.StartActivity("DataCollectedEvent", ActivityKind.Consumer);
            activity?.SetTag("connection.id", @event.ConnectionId);
            activity?.SetTag("device.id", @event.DeviceId);
            activity?.SetTag("register.count", @event.RegisterData.Count);

            var stopwatch = Stopwatch.StartNew();

            try
            {
                _logger.LogDebug("🔄 开始处理数据采集事件: {ConnectionId}/{DeviceId}",
                    @event.ConnectionId, @event.DeviceId);

                var deviceConfig = await LoadDeviceConfigAsync(@event.ConnectionId, @event.DeviceId);
                if (deviceConfig == null)
                {
                    activity?.SetStatus(ActivityStatusCode.Error, "设备配置未找到");
                    _logger.LogWarning("找不到设备配置: {DeviceId}", @event.DeviceId);
                    return null;
                }

                var dataBatch = new ModbusDataBatch
                {
                    ConnectionId = @event.ConnectionId,
                    DeviceId = @event.DeviceId,
                    Timestamp = @event.Timestamp
                };

                var connectionConfig = await LoadConnectionConfigAsync(@event.ConnectionId);
                var byteOrder = connectionConfig?.ByteOrder ?? "ABCD";
                var registerType = connectionConfig?.RegisterType?.ToLower() ?? "holding";

                activity?.SetTag("byte.order", byteOrder);
                activity?.SetTag("register.type", registerType);

                _logger.LogDebug("解析参数: RegisterType={Type}, 参数总数={Count}, 启用数={EnabledCount}",
                    registerType,
                    deviceConfig.Parameters.Count,
                    deviceConfig.Parameters.Count(p => p.Enabled));

                var parsedCount = 0;
                var failedCount = 0;

                foreach (var param in deviceConfig.Parameters)
                {
                    if (!param.Enabled)
                    {
                        _logger.LogTrace("跳过未启用的参数: {Code}", param.Code);
                        continue;
                    }

                    try
                    {
                        ModbusDataPoint dataPoint;

                        if (IsBitType(registerType, param.DataType))
                        {
                            dataPoint = ParseBitParameter(
                                param,
                                @event.RegisterData,
                                @event.ConnectionId,
                                @event.DeviceId,
                                @event.Timestamp);
                        }
                        else
                        {
                            dataPoint = ParseRegisterParameter(
                                param,
                                @event.RegisterData,
                                byteOrder,
                                @event.ConnectionId,
                                @event.DeviceId,
                                @event.Timestamp);
                        }

                        dataBatch.DataPoints.Add(dataPoint);
                        parsedCount++;

                        // ✅ 检查并发布值变化事件 - 对 bit_map 参数使用 rawValue
                        if (param.OnChange)
                        {
                            await CheckAndPublishValueChangeAsync(
                                dataPoint,
                                param,
                                cancellationToken);
                        }

                        _logger.LogTrace("✓ 参数解析成功: {Code}={Value}",
                            param.Code, dataPoint.ParsedValue);
                    }
                    catch (Exception ex)
                    {
                        failedCount++;
                        _logger.LogError(ex,
                            "解析参数失败: {ParamCode}, 地址: {Address}",
                            param.Code,
                            string.Join(",", param.Address));
                    }
                }

                _cacheService.Cache(@event.ConnectionId, @event.DeviceId, dataBatch);

                stopwatch.Stop();

                AppTelemetry.Metrics.EventProcessingDuration.Record(stopwatch.Elapsed.TotalMilliseconds,
                    new KeyValuePair<string, object?>("event.type", "DataCollected"),
                    new KeyValuePair<string, object?>("connection.id", @event.ConnectionId),
                    new KeyValuePair<string, object?>("device.id", @event.DeviceId));

                activity?.SetTag("parsed.count", parsedCount);
                activity?.SetTag("failed.count", failedCount);
                activity?.SetTag("duration.ms", stopwatch.Elapsed.TotalMilliseconds);
                activity?.SetStatus(ActivityStatusCode.Ok);

                _logger.LogDebug("✓ 数据已缓存: {ConnectionId}/{DeviceId}, 参数数量: {Count}",
                    @event.ConnectionId, @event.DeviceId, dataBatch.DataPoints.Count);

                return new DataParsedEvent(dataBatch);
            }
            catch (Exception ex)
            {
                stopwatch.Stop();

                activity?.AddException(ex);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);

                _logger.LogError(ex, "处理数据采集事件失败: {ConnectionId}/{DeviceId}",
                    @event.ConnectionId, @event.DeviceId);
                return null;
            }
        }

        private bool IsBitType(string registerType, string dataType)
        {
            if (registerType == "coil" || registerType == "discrete_input")
            {
                return true;
            }

            if (dataType?.ToLower() == "bit")
            {
                return true;
            }

            return false;
        }

        private ModbusDataPoint ParseBitParameter(
            ParameterConfig param,
            Dictionary<int, ushort> registerData,
            string connectionId,
            string deviceId,
            DateTime timestamp)
        {
            int address = param.Address[0];

            if (!registerData.ContainsKey(address))
            {
                throw new KeyNotFoundException(
                    $"参数 {param.Code} 需要的地址 {address} 未被读取");
            }

            ushort bitValue = registerData[address];
            bool boolValue = bitValue != 0;

            _logger.LogTrace(
                "解析位参数: {Code}, 地址={Addr}, 原始值=0x{Raw:X4}, 布尔值={Bool}",
                param.Code, address, bitValue, boolValue);

            object parsedValue = boolValue;

            if (param.EnumMap != null && param.EnumMap.Count > 0)
            {
                var key = bitValue.ToString();
                parsedValue = param.EnumMap.ContainsKey(key)
                    ? param.EnumMap[key]
                    : boolValue;

                _logger.LogTrace("应用枚举映射: {Code}, Key={Key}, Value={Value}",
                    param.Code, key, parsedValue);
            }

            return new ModbusDataPoint
            {
                ConnectionId = connectionId,
                DeviceId = deviceId,
                ParameterCode = param.Code,
                ParameterName = param.Name,
                RawValue = bitValue,
                ParsedValue = parsedValue,
                Unit = param.Unit,
                Timestamp = timestamp,
                Metadata = new Dictionary<string, object>
                {
                    ["DataType"] = "bit",
                    ["Address"] = param.Address,
                    ["BoolValue"] = boolValue
                }
            };
        }

        private ModbusDataPoint ParseRegisterParameter(
            ParameterConfig param,
            Dictionary<int, ushort> registerData,
            string byteOrder,
            string connectionId,
            string deviceId,
            DateTime timestamp)
        {
            int paramStartAddress = param.Address[0];
            int requiredRegisters = GetRequiredRegisters(param.DataType);

            for (int i = 0; i < requiredRegisters; i++)
            {
                if (!registerData.ContainsKey(paramStartAddress + i))
                {
                    throw new KeyNotFoundException(
                        $"参数 {param.Code} 需要的寄存器地址 {paramStartAddress + i} 未被读取");
                }
            }

            ushort[] paramRegisters = new ushort[requiredRegisters];
            for (int i = 0; i < requiredRegisters; i++)
            {
                paramRegisters[i] = registerData[paramStartAddress + i];
            }

            _logger.LogTrace(
                "解析寄存器参数: {Code}, 地址={Addr}, 需要={Required}个寄存器, 值=[{Values}]",
                param.Code, paramStartAddress, requiredRegisters,
                string.Join(",", paramRegisters.Select(r => $"0x{r:X4}")));

            var rawValue = _dataParser.ParseValue(
                paramRegisters,
                0,
                param.DataType,
                byteOrder,
                param.Scale,
                param.Offset);

            object parsedValue = rawValue;

            if (param.BitMap != null && param.DataType == "uint16")
            {
                parsedValue = _dataParser.ParseBitMap((ushort)rawValue, param.BitMap);
            }
            else if (param.EnumMap != null && param.DataType == "uint16")
            {
                var key = rawValue.ToString()!;
                parsedValue = param.EnumMap.ContainsKey(key)
                    ? param.EnumMap[key]
                    : rawValue;
            }
            else if (rawValue is double d)
            {
                parsedValue = Math.Round(d, param.Precision);
            }

            return new ModbusDataPoint
            {
                ConnectionId = connectionId,
                DeviceId = deviceId,
                ParameterCode = param.Code,
                ParameterName = param.Name,
                RawValue = rawValue,
                ParsedValue = parsedValue,
                Unit = param.Unit,
                Timestamp = timestamp,
                Metadata = new Dictionary<string, object>
                {
                    ["DataType"] = param.DataType,
                    ["Address"] = param.Address,
                    ["Scale"] = param.Scale,
                    ["Offset"] = param.Offset
                }
            };
        }

        private int GetRequiredRegisters(string dataType)
        {
            return dataType.ToLower() switch
            {
                "float32" => 2,
                "uint32" => 2,
                "int32" => 2,
                "uint16" => 1,
                "int16" => 1,
                "bit" => 1,
                _ => 1
            };
        }

        /// <summary>
        /// 对 bit_map 参数使用 rawValue 进行比较
        /// </summary>
        private async Task CheckAndPublishValueChangeAsync(
            ModbusDataPoint dataPoint,
            ParameterConfig param,
            CancellationToken cancellationToken)
        {
            var key = $"{dataPoint.ConnectionId}:{dataPoint.DeviceId}:{dataPoint.ParameterCode}";

            // 对于 bit_map 参数,用 rawValue 比较
            object valueToCompare = (param.BitMap != null && param.DataType == "uint16")
                ? dataPoint.RawValue
                : dataPoint.ParsedValue;

            _logger.LogTrace("🔍 检查参数变化: Key={Key}, RawValue={Raw}, ParsedValue={Parsed}, CompareValue={Compare}",
                key, dataPoint.RawValue, dataPoint.ParsedValue, valueToCompare);

            if (_valueTracker.TryGetLastValue(key, out var lastValue))
            {
                bool changed = !ValuesEqual(lastValue, valueToCompare, param.Precision);

                if (changed)
                {
                    _logger.LogInformation("📢 发布参数变化事件: {ParamCode}, 0x{OldValue:X4} → 0x{NewValue:X4}",
                        dataPoint.ParameterCode, lastValue, valueToCompare);

                    var changeEvent = new ParameterValueChangedEvent(
                        dataPoint.ConnectionId,
                        dataPoint.DeviceId,
                        dataPoint.ParameterCode,
                        dataPoint.ParameterName,
                        lastValue,
                        valueToCompare,
                        dataPoint.Unit,
                        DateTime.Now,
                        dataPoint
                    );

                    await _messageBus.PublishAsync(changeEvent);
                }
                else
                {
                    _logger.LogTrace("⏸️ 参数值未变化: {ParamCode} = 0x{Value:X4}",
                        dataPoint.ParameterCode, valueToCompare);
                }
            }
            else
            {
                _logger.LogDebug("📝 首次记录参数值: {ParamCode} = 0x{Value:X4}",
                    dataPoint.ParameterCode, valueToCompare);
            }

            // 更新追踪值
            _valueTracker.UpdateValue(key, valueToCompare);
        }

        /// <summary>
        /// 值比较逻辑
        /// </summary>
        private bool ValuesEqual(object oldValue, object newValue, int precision)
        {
            // 浮点数比较
            if (oldValue is double oldDouble && newValue is double newDouble)
            {
                var threshold = Math.Pow(10, -precision);
                return Math.Abs(oldDouble - newDouble) < threshold;
            }

            // 其他类型直接比较
            return Equals(oldValue, newValue);
        }

        private async Task<DeviceConfig?> LoadDeviceConfigAsync(string connectionId, string deviceId)
        {
            return await _configCache.GetDeviceConfigAsync(connectionId, deviceId);
        }

        private async Task<ModbusConnectionConfig?> LoadConnectionConfigAsync(string connectionId)
        {
            return await _configCache.GetConnectionConfigAsync(connectionId);
        }
    }
}