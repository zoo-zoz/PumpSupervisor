using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PumpSupervisor.Domain.Models;
using PumpSupervisor.Infrastructure.Modbus.Commands;
using PumpSupervisor.Infrastructure.Modbus.Queries;
using System.Text.Json;
using Wolverine;

namespace PumpSupervisor.Application.Services
{
    public interface IModbusApiService
    {
        // 按需读取设备数据
        Task<ModbusCommandResult> ReadDeviceDataAsync(
            string connectionId,
            string deviceId,
            CancellationToken cancellationToken = default);

        // 写入数据
        Task<ModbusCommandResult> WriteDataAsync(
            string connectionId,
            string deviceId,
            int startAddress,
            ushort[] values,
            CancellationToken cancellationToken = default);

        // 写入单个寄存器
        Task<ModbusCommandResult> WriteSingleRegisterAsync(
            string connectionId,
            string deviceId,
            int address,
            ushort value,
            CancellationToken cancellationToken = default);

        // 获取设备当前数据
        Task<DeviceDataResult> GetDeviceDataAsync(
            string connectionId,
            string deviceId,
            CancellationToken cancellationToken = default);
    }

    public class ModbusApiService : IModbusApiService
    {
        private readonly ILogger<ModbusApiService> _logger;
        private readonly IMessageBus _messageBus;
        private readonly IConfiguration _configuration;
        private List<ModbusConnectionConfig> _connectionConfigs = new();

        public ModbusApiService(
            ILogger<ModbusApiService> logger,
            IMessageBus messageBus,
            IConfiguration configuration)
        {
            _logger = logger;
            _messageBus = messageBus;
            _configuration = configuration;

            // 加载配置
            LoadConfiguration();
        }

        public async Task<ModbusCommandResult> ReadDeviceDataAsync(
            string connectionId,
            string deviceId,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var connection = _connectionConfigs.FirstOrDefault(c => c.Id == connectionId);
                if (connection == null)
                {
                    return new ModbusCommandResult(false, $"找不到连接: {connectionId}");
                }

                var device = connection.Devices.FirstOrDefault(d => d.Id == deviceId);
                if (device == null)
                {
                    return new ModbusCommandResult(false, $"找不到设备: {deviceId}");
                }

                // 发送读取命令自动读取所有 read_blocks
                var command = new ReadModbusDataCommand(
                    connectionId,
                    deviceId,
                    Priority: 2 // 按需读取优先级为2（中等）
                );

                var result = await _messageBus.InvokeAsync<ModbusCommandResult>(
                    command,
                    cancellationToken);

                _logger.LogInformation("按需读取完成: {ConnectionId}/{DeviceId}, 成功: {Success}",
                    connectionId, deviceId, result.Success);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "按需读取失败: {ConnectionId}/{DeviceId}",
                    connectionId, deviceId);
                return new ModbusCommandResult(false, ex.Message);
            }
        }

        public async Task<ModbusCommandResult> WriteDataAsync(
            string connectionId,
            string deviceId,
            int startAddress,
            ushort[] values,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var command = new WriteModbusDataCommand(
                    connectionId,
                    deviceId,
                    startAddress,
                    values,
                    Priority: 10 // 写入操作高优先级
                );

                var result = await _messageBus.InvokeAsync<ModbusCommandResult>(
                    command,
                    cancellationToken);

                _logger.LogInformation("写入数据: {ConnectionId}/{DeviceId}, 地址: {Address}, 数量: {Count}, 结果: {Success}",
                    connectionId, deviceId, startAddress, values.Length, result.Success);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "写入数据失败");
                return new ModbusCommandResult(false, ex.Message);
            }
        }

        public async Task<ModbusCommandResult> WriteSingleRegisterAsync(
            string connectionId,
            string deviceId,
            int address,
            ushort value,
            CancellationToken cancellationToken = default)
        {
            return await WriteDataAsync(connectionId, deviceId, address, new[] { value }, cancellationToken);
        }

        public async Task<DeviceDataResult> GetDeviceDataAsync(
            string connectionId,
            string deviceId,
            CancellationToken cancellationToken = default)
        {
            var query = new GetDeviceDataQuery(connectionId, deviceId);
            return await _messageBus.InvokeAsync<DeviceDataResult>(query, cancellationToken);
        }

        private void LoadConfiguration()
        {
            try
            {
                var configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "readModbus.json");
                var json = File.ReadAllText(configPath);

                var config = JsonSerializer.Deserialize<ModbusConfig>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                _connectionConfigs = config?.Connections ?? new List<ModbusConnectionConfig>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "加载配置失败");
            }
        }
    }
}