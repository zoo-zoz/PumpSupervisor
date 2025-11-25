using Microsoft.Extensions.Logging;
using PumpSupervisor.Domain.Models;
using PumpSupervisor.Infrastructure.Modbus.Commands;
using System.Text.Json;

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

        public ModbusWriteCommandHandler(
            IModbusConnectionManager connectionManager,
            ILogger<ModbusWriteCommandHandler> logger)
        {
            _connectionManager = connectionManager;
            _logger = logger;
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

                // 1. 加载连接配置
                var config = await LoadConnectionConfigAsync(command.ConnectionId);
                if (config == null)
                {
                    var errorMsg = $"找不到连接配置: {command.ConnectionId}";
                    _logger.LogError(errorMsg);
                    return new ModbusCommandResult(false, errorMsg);
                }

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

        /// <summary>
        /// 从配置文件加载指定的连接配置
        /// </summary>
        private async Task<ModbusConnectionConfig?> LoadConnectionConfigAsync(string connectionId)
        {
            try
            {
                var configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "readModbus.json");
                if (!File.Exists(configPath))
                {
                    _logger.LogError("配置文件不存在: {Path}", configPath);
                    return null;
                }

                var json = await File.ReadAllTextAsync(configPath);

                var config = JsonSerializer.Deserialize<ModbusConfig>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                var connectionConfig = config?.Connections?.FirstOrDefault(c => c.Id == connectionId);

                if (connectionConfig == null)
                {
                    _logger.LogWarning("在配置中未找到连接: {ConnectionId}", connectionId);
                }

                return connectionConfig;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "加载连接配置失败: {ConnectionId}", connectionId);
                return null;
            }
        }
    }
}