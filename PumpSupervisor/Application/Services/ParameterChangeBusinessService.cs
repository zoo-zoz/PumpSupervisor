using Microsoft.Extensions.Logging;
using PumpSupervisor.Domain.Models;
using PumpSupervisor.Infrastructure.Cache;
using PumpSupervisor.Infrastructure.Modbus;
using PumpSupervisor.Infrastructure.Modbus.Commands;
using PumpSupervisor.Infrastructure.Modbus.DataParser;
using System.Collections.Concurrent;
using Wolverine;

namespace PumpSupervisor.Application.Services
{
    /// <summary>
    /// 参数变化业务逻辑服务
    /// </summary>
    public class ParameterChangeBusinessService
    {
        private readonly ILogger<ParameterChangeBusinessService> _logger;
        private readonly IMessageBus _messageBus;
        private readonly IModbusConnectionManager _connectionManager;
        private readonly IModbusConfigCacheService _configCache;
        private readonly IModbusDataParser _dataParser;
        private readonly ConcurrentDictionary<string, DateTime> _lastProcessed = new();
        private readonly TimeSpan _debounceInterval = TimeSpan.FromMilliseconds(50);

        public ParameterChangeBusinessService(
            ILogger<ParameterChangeBusinessService> logger,
            IMessageBus messageBus,
            IModbusConnectionManager connectionManager,
            IModbusConfigCacheService configCache,
            IModbusDataParser dataParser)
        {
            _logger = logger;
            _messageBus = messageBus;
            _connectionManager = connectionManager;
            _configCache = configCache;
            _dataParser = dataParser;
        }

        #region ✨ 新增:支持 bit_map 子级读取

        /// <summary>
        /// 读取指定参数的值 - 支持 bit_map 子级 code
        /// </summary>
        /// <param name="connectionId">连接ID</param>
        /// <param name="deviceId">设备ID</param>
        /// <param name="parameterCode">
        /// 参数代码,支持两种格式:
        /// 1. 顶层参数: "emulsionMasterPressureWarningState"
        /// 2. bit_map子级: "emulsionMasterPressureAlarm_HighAlarm"
        /// </param>
        /// <param name="cancellationToken">取消令牌</param>
        /// <returns>
        /// - 顶层参数:返回解析后的值(可能是 Dictionary&lt;string,bool&gt;)
        /// - bit_map子级:返回 bool 值
        /// </returns>
        public async Task<object?> ReadParameterValueAsync(
            string connectionId,
            string deviceId,
            string parameterCode,
            CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogDebug("📖 读取参数值: {ConnectionId}/{DeviceId}/{ParamCode}",
                    connectionId, deviceId, parameterCode);

                // 1. 获取设备配置
                var deviceConfig = await _configCache.GetDeviceConfigAsync(connectionId, deviceId);
                if (deviceConfig == null)
                {
                    _logger.LogWarning("⚠️ 设备配置未找到: {ConnectionId}/{DeviceId}",
                        connectionId, deviceId);
                    return null;
                }

                // 2. 尝试作为顶层参数查找
                var param = deviceConfig.Parameters.FirstOrDefault(p => p.Code == parameterCode);

                if (param != null)
                {
                    // ✅ 找到顶层参数,直接读取
                    return await ReadTopLevelParameterAsync(
                        connectionId, deviceId, param, cancellationToken);
                }

                // 3. 未找到顶层参数,尝试作为 bit_map 子级查找
                var (parentParam, bitCode) = FindBitMapParent(deviceConfig, parameterCode);

                if (parentParam != null && bitCode != null)
                {
                    // ✅ 找到 bit_map 子级,读取父参数并提取子级值
                    return await ReadBitMapSubValueAsync(
                        connectionId, deviceId, parentParam, bitCode, cancellationToken);
                }

                _logger.LogWarning("⚠️ 参数未找到: {ParamCode} (既不是顶层参数,也不是bit_map子级)",
                    parameterCode);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ 读取参数值失败: {ParamCode}", parameterCode);
                return null;
            }
        }

        /// <summary>
        /// 批量读取参数值 - 支持混合顶层参数和 bit_map 子级
        /// </summary>
        public async Task<Dictionary<string, object?>> ReadParameterValuesAsync(
            string connectionId,
            string deviceId,
            string[] parameterCodes,
            CancellationToken cancellationToken = default)
        {
            var result = new Dictionary<string, object?>();

            try
            {
                _logger.LogDebug("📖 批量读取参数: {ConnectionId}/{DeviceId}, 参数数: {Count}",
                    connectionId, deviceId, parameterCodes.Length);

                // 一次性读取所有寄存器
                var registerData = await ReadRegistersAsync(connectionId, deviceId, cancellationToken);
                if (registerData == null)
                {
                    return result;
                }

                var deviceConfig = await _configCache.GetDeviceConfigAsync(connectionId, deviceId);
                if (deviceConfig == null)
                {
                    return result;
                }

                var connectionConfig = await _configCache.GetConnectionConfigAsync(connectionId);
                var byteOrder = connectionConfig?.ByteOrder ?? "ABCD";

                // 解析每个参数
                foreach (var paramCode in parameterCodes)
                {
                    // 1. 尝试作为顶层参数
                    var param = deviceConfig.Parameters.FirstOrDefault(p => p.Code == paramCode);

                    if (param != null)
                    {
                        var value = ParseParameterValue(param, registerData, byteOrder);
                        result[paramCode] = value;
                        continue;
                    }

                    // 2. 尝试作为 bit_map 子级
                    var (parentParam, bitCode) = FindBitMapParent(deviceConfig, paramCode);

                    if (parentParam != null && bitCode != null)
                    {
                        var parentValue = ParseParameterValue(parentParam, registerData, byteOrder);

                        if (parentValue is Dictionary<string, bool> bitMap)
                        {
                            result[paramCode] = bitMap.GetValueOrDefault(paramCode, false);
                        }
                        else
                        {
                            _logger.LogWarning("⚠️ 父参数不是 bit_map 类型: {ParamCode}", paramCode);
                            result[paramCode] = null;
                        }
                        continue;
                    }

                    // 3. 未找到
                    _logger.LogWarning("⚠️ 参数未找到: {ParamCode}", paramCode);
                    result[paramCode] = null;
                }

                _logger.LogInformation("✅ 批量读取成功: {Count}/{Total} 个参数",
                    result.Count(x => x.Value != null), parameterCodes.Length);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ 批量读取参数失败");
                return result;
            }
        }

        #endregion ✨ 新增:支持 bit_map 子级读取

        #region 私有辅助方法

        /// <summary>
        /// 读取顶层参数
        /// </summary>
        private async Task<object?> ReadTopLevelParameterAsync(
            string connectionId,
            string deviceId,
            ParameterConfig param,
            CancellationToken cancellationToken)
        {
            var registerData = await ReadRegistersAsync(connectionId, deviceId, cancellationToken);
            if (registerData == null)
            {
                return null;
            }

            var connectionConfig = await _configCache.GetConnectionConfigAsync(connectionId);
            var byteOrder = connectionConfig?.ByteOrder ?? "ABCD";

            return ParseParameterValue(param, registerData, byteOrder);
        }

        /// <summary>
        /// 读取 bit_map 子级值
        /// </summary>
        private async Task<object?> ReadBitMapSubValueAsync(
            string connectionId,
            string deviceId,
            ParameterConfig parentParam,
            string bitCode,
            CancellationToken cancellationToken)
        {
            _logger.LogDebug("📖 读取 bit_map 子级: ParentCode={ParentCode}, BitCode={BitCode}",
                parentParam.Code, bitCode);

            var registerData = await ReadRegistersAsync(connectionId, deviceId, cancellationToken);
            if (registerData == null)
            {
                return null;
            }

            var connectionConfig = await _configCache.GetConnectionConfigAsync(connectionId);
            var byteOrder = connectionConfig?.ByteOrder ?? "ABCD";

            var parentValue = ParseParameterValue(parentParam, registerData, byteOrder);

            if (parentValue is Dictionary<string, bool> bitMap)
            {
                if (bitMap.TryGetValue(bitCode, out var bitValue))
                {
                    _logger.LogDebug("✅ bit_map 子级值: {BitCode}={Value}", bitCode, bitValue);
                    return bitValue;
                }
                else
                {
                    _logger.LogWarning("⚠️ bit_map 中未找到子级: {BitCode}", bitCode);
                    return null;
                }
            }
            else
            {
                _logger.LogWarning("⚠️ 父参数不是 bit_map 类型: {ParentCode}", parentParam.Code);
                return null;
            }
        }

        /// <summary>
        /// 查找 bit_map 父参数
        /// </summary>
        /// <param name="deviceConfig">设备配置</param>
        /// <param name="bitCode">bit_map 子级 code</param>
        /// <returns>(父参数配置, bit_map子级code) 或 (null, null)</returns>
        private (ParameterConfig? parentParam, string? bitCode) FindBitMapParent(
            DeviceConfig deviceConfig,
            string bitCode)
        {
            foreach (var param in deviceConfig.Parameters)
            {
                if (param.BitMap == null || param.BitMap.Count == 0)
                {
                    continue;
                }

                // 检查 bit_map 中是否有匹配的 code
                foreach (var kvp in param.BitMap)
                {
                    if (kvp.Value.Code == bitCode)
                    {
                        _logger.LogDebug("✅ 找到 bit_map 父参数: ParentCode={ParentCode}, BitIndex={BitIndex}",
                            param.Code, kvp.Key);
                        return (param, bitCode);
                    }
                }
            }

            return (null, null);
        }

        /// <summary>
        /// 解析参数值
        /// </summary>
        private object? ParseParameterValue(
            ParameterConfig param,
            Dictionary<int, ushort> registerData,
            string byteOrder)
        {
            try
            {
                int startAddress = param.Address[0];
                int requiredRegisters = GetRequiredRegisters(param.DataType);

                // 检查所需寄存器是否都存在
                for (int i = 0; i < requiredRegisters; i++)
                {
                    if (!registerData.ContainsKey(startAddress + i))
                    {
                        _logger.LogWarning("⚠️ 缺少寄存器地址: {Address}",
                            startAddress + i);
                        return null;
                    }
                }

                // 提取寄存器值
                var registers = new ushort[requiredRegisters];
                for (int i = 0; i < requiredRegisters; i++)
                {
                    registers[i] = registerData[startAddress + i];
                }

                // 解析原始值
                var rawValue = _dataParser.ParseValue(
                    registers, 0, param.DataType, byteOrder, param.Scale, param.Offset);

                // 应用特殊处理
                object parsedValue = rawValue;

                if (param.BitMap != null && param.DataType == "uint16")
                {
                    // ✅ 解析 bit_map
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

                return parsedValue;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ 解析参数值失败: {ParamCode}", param.Code);
                return null;
            }
        }

        /// <summary>
        /// 获取数据类型所需的寄存器数量
        /// </summary>
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

        #endregion 私有辅助方法

        #region 原有方法 - 寄存器读写

        /// <summary>
        /// 读取指定设备的所有寄存器
        /// </summary>
        public async Task<Dictionary<int, ushort>?> ReadRegistersAsync(
            string connectionId,
            string deviceId,
            CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogDebug("📖 业务逻辑读取: Connection={ConnectionId}, Device={DeviceId}",
                    connectionId, deviceId);

                var command = new ReadModbusDataCommand(
                    connectionId,
                    deviceId,
                    Priority: 10  // 高优先级
                );

                var result = await _messageBus.InvokeAsync<ModbusCommandResult>(
                    command,
                    cancellationToken,
                    timeout: TimeSpan.FromSeconds(10));

                if (result?.Success == true)
                {
                    var registerData = result.GetRegisterData();
                    _logger.LogDebug("✅ 读取成功: 寄存器数={Count}", registerData?.Count ?? 0);
                    return registerData;
                }
                else
                {
                    _logger.LogWarning("⚠️ 读取失败: {Message}", result?.Message);
                    return null;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ 读取寄存器异常");
                return null;
            }
        }

        /// <summary>
        /// 写入指定设备的寄存器
        /// </summary>
        public async Task<bool> WriteRegistersAsync(
            string connectionId,
            string deviceId,
            int startAddress,
            ushort[] values,
            CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogInformation("✍️ 业务逻辑写入: Connection={ConnectionId}, Device={DeviceId}, Address={Address}, Values=[{Values}]",
                    connectionId, deviceId, startAddress, string.Join(",", values.Select(v => $"0x{v:X4}")));

                var command = new WriteModbusDataCommand(
                    connectionId,
                    deviceId,
                    startAddress,
                    values,
                    Priority: 10  // 高优先级
                );

                var result = await _messageBus.InvokeAsync<ModbusCommandResult>(
                    command,
                    cancellationToken,
                    timeout: TimeSpan.FromSeconds(10));

                if (result?.Success == true)
                {
                    _logger.LogInformation("✅ 写入成功");
                    return true;
                }
                else
                {
                    _logger.LogWarning("⚠️ 写入失败: {Message}", result?.Message);
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ 写入寄存器异常");
                return false;
            }
        }

        /// <summary>
        /// 写入指定连接的寄存器（无 deviceId）
        /// 适用于直接对连接进行写入操作
        /// </summary>
        public async Task<bool> WriteRegistersAsync(
            string connectionId,
            int startAddress,
            ushort[] values,
            CancellationToken cancellationToken = default)
        {
            return await WriteRegistersAsync(connectionId, string.Empty, startAddress, values, cancellationToken);
        }

        /// <summary>
        /// 写入单个寄存器
        /// </summary>
        public async Task<bool> WriteSingleRegisterAsync(
            string connectionId,
            string deviceId,
            int address,
            ushort value,
            CancellationToken cancellationToken = default)
        {
            return await WriteRegistersAsync(connectionId, deviceId, address, new[] { value }, cancellationToken);
        }

        /// <summary>
        /// 写入单个寄存器（无 deviceId）
        /// </summary>
        public async Task<bool> WriteSingleRegisterAsync(
            string connectionId,
            int address,
            ushort value,
            CancellationToken cancellationToken = default)
        {
            return await WriteRegistersAsync(connectionId, string.Empty, address, new[] { value }, cancellationToken);
        }

        #endregion 原有方法 - 寄存器读写

        #region 原有方法 - 按寄存器地址读取

        /// <summary>
        /// 读取单个寄存器的原始值
        /// </summary>
        public async Task<ushort?> ReadSingleRegisterAsync(
            string connectionId,
            string deviceId,
            int address,
            CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogDebug("📖 读取单个寄存器: {ConnectionId}/{DeviceId}, Address={Address}",
                    connectionId, deviceId, address);

                var registerData = await ReadRegistersAsync(connectionId, deviceId, cancellationToken);

                if (registerData != null && registerData.TryGetValue(address, out var value))
                {
                    _logger.LogDebug("✅ 寄存器值: Address={Address}, Value=0x{Value:X4}",
                        address, value);
                    return value;
                }

                _logger.LogWarning("⚠️ 寄存器地址不存在或未读取: {Address}", address);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ 读取单个寄存器失败: Address={Address}", address);
                return null;
            }
        }

        /// <summary>
        /// 读取多个寄存器的原始值
        /// </summary>
        public async Task<Dictionary<int, ushort>?> ReadMultipleRegistersAsync(
            string connectionId,
            string deviceId,
            int[] addresses,
            CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogDebug("📖 读取多个寄存器: {ConnectionId}/{DeviceId}, Addresses=[{Addresses}]",
                    connectionId, deviceId, string.Join(",", addresses));

                var registerData = await ReadRegistersAsync(connectionId, deviceId, cancellationToken);
                if (registerData == null)
                {
                    return null;
                }

                var result = new Dictionary<int, ushort>();
                foreach (var addr in addresses)
                {
                    if (registerData.TryGetValue(addr, out var value))
                    {
                        result[addr] = value;
                    }
                    else
                    {
                        _logger.LogWarning("⚠️ 寄存器地址不存在: {Address}", addr);
                    }
                }

                _logger.LogInformation("✅ 读取寄存器成功: {Count}/{Total} 个地址",
                    result.Count, addresses.Length);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ 读取多个寄存器失败");
                return null;
            }
        }

        /// <summary>
        /// 读取寄存器并解析为指定数据类型
        /// </summary>
        public async Task<object?> ReadAndParseRegistersAsync(
            string connectionId,
            string deviceId,
            int[] addresses,
            string dataType,
            double scale = 1.0,
            double offset = 0.0,
            CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogDebug(
                    "📖 读取并解析寄存器: {ConnectionId}/{DeviceId}, Addresses=[{Addresses}], DataType={DataType}",
                    connectionId, deviceId, string.Join(",", addresses), dataType);

                // 1. 读取寄存器
                var registerData = await ReadMultipleRegistersAsync(
                    connectionId, deviceId, addresses, cancellationToken);

                if (registerData == null || registerData.Count == 0)
                {
                    return null;
                }

                // 2. 构建连续的寄存器数组
                var registers = new ushort[addresses.Length];
                for (int i = 0; i < addresses.Length; i++)
                {
                    if (registerData.TryGetValue(addresses[i], out var value))
                    {
                        registers[i] = value;
                    }
                    else
                    {
                        _logger.LogWarning("⚠️ 缺少寄存器地址: {Address}", addresses[i]);
                        return null;
                    }
                }

                // 3. 获取字节序配置
                var connectionConfig = await _configCache.GetConnectionConfigAsync(connectionId);
                var byteOrder = connectionConfig?.ByteOrder ?? "ABCD";

                // 4. 解析数据
                var parsedValue = _dataParser.ParseValue(
                    registers, 0, dataType, byteOrder, scale, offset);

                _logger.LogInformation(
                    "✅ 解析成功: Addresses=[{Addresses}], DataType={DataType}, Value={Value}",
                    string.Join(",", addresses), dataType, parsedValue);

                return parsedValue;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ 读取并解析寄存器失败");
                return null;
            }
        }

        #endregion 原有方法 - 按寄存器地址读取

        #region 原有方法 - 防抖处理

        /// <summary>
        /// 防抖处理：避免短时间内重复处理同一个变化
        /// </summary>
        public bool ShouldProcess(string key)
        {
            var now = DateTime.Now;

            if (_lastProcessed.TryGetValue(key, out var lastTime))
            {
                if (now - lastTime < _debounceInterval)
                {
                    _logger.LogDebug("⏭️ 跳过重复处理: {Key}, 距上次处理 {Ms}ms",
                        key, (now - lastTime).TotalMilliseconds);
                    return false;
                }
            }

            _lastProcessed[key] = now;
            return true;
        }

        /// <summary>
        /// 清理过期的去重记录
        /// </summary>
        public void CleanupDebounceCache()
        {
            var cutoffTime = DateTime.Now - TimeSpan.FromMinutes(5);
            var keysToRemove = _lastProcessed
                .Where(kvp => kvp.Value < cutoffTime)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in keysToRemove)
            {
                _lastProcessed.TryRemove(key, out _);
            }

            if (keysToRemove.Count > 0)
            {
                _logger.LogDebug("🧹 清理去重缓存: 移除 {Count} 条记录", keysToRemove.Count);
            }
        }

        #endregion 原有方法 - 防抖处理
    }
}