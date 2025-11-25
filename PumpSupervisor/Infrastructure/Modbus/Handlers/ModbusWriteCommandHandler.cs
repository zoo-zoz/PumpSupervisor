using Microsoft.Extensions.Logging;
using PumpSupervisor.Infrastructure.Cache;
using PumpSupervisor.Infrastructure.Modbus.Commands;

namespace PumpSupervisor.Infrastructure.Modbus.Handlers
{
    /// <summary>
    /// Modbus 写入命令处理器
    /// 使用 IModbusConnectionManager 统一管理连接
    /// </summary>
    public class ModbusWriteCommandHandler
    {
        private readonly IModbusConnectionManager _connectionManager;
        private readonly ILogger<ModbusWriteCommandHandler> _logger;
        private readonly IModbusConfigCacheService _configCache; // ✅ 新增

        public ModbusWriteCommandHandler(
            IModbusConnectionManager connectionManager,
            ILogger<ModbusWriteCommandHandler> logger,
            IModbusConfigCacheService configCache) // ✅ 新增
        {
            _connectionManager = connectionManager;
            _logger = logger;
            _configCache = configCache; // ✅ 新增
        }

        /// <summary>
        /// Wolverine 自动识别的命令处理方法
        /// </summary>
        public async Task<ModbusCommandResult> Handle(
            WriteModbusDataCommand command,
            CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogDebug("开始处理写入命令: Connection={ConnectionId}, Address={Address}, Count={Count}",
                    command.ConnectionId, command.StartAddress, command.Values.Length);

                // ========== ✅ 修改：使用缓存服务加载配置 ==========
                var config = await _configCache.GetConnectionConfigAsync(command.ConnectionId);
                if (config == null)
                {
                    var errorMsg = $"找不到连接配置: {command.ConnectionId}";
                    _logger.LogError(errorMsg);
                    return new ModbusCommandResult(false, errorMsg);
                }
                // ===================================================

                // 2. 通过连接管理器获取连接（自动处理连接复用）
                var connection = await _connectionManager.GetConnectionAsync(
                    command.ConnectionId,
                    cancellationToken);

                // 3. 根据写入数据量选择写入方法
                if (command.Values.Length == 1)
                {
                    // 写单个寄存器
                    await connection.WriteSingleRegisterAsync(
                        (byte)config.SlaveId,
                        (ushort)command.StartAddress,
                        command.Values[0],
                        cancellationToken);

                    _logger.LogInformation("写入单个寄存器成功: Connection={ConnectionId}, Address={Address}, Value={Value}",
                        command.ConnectionId, command.StartAddress, command.Values[0]);
                }
                else
                {
                    // 写多个寄存器
                    await connection.WriteMultipleRegistersAsync(
                        (byte)config.SlaveId,
                        (ushort)command.StartAddress,
                        command.Values,
                        cancellationToken);

                    _logger.LogInformation("写入多个寄存器成功: Connection={ConnectionId}, Address={Address}, Count={Count}",
                        command.ConnectionId, command.StartAddress, command.Values.Length);
                }

                return new ModbusCommandResult(true, "写入成功");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "写入Modbus数据失败: Connection={ConnectionId}, Address={Address}",
                    command.ConnectionId, command.StartAddress);

                return new ModbusCommandResult(false, $"写入失败: {ex.Message}");
            }
        }
    }
}