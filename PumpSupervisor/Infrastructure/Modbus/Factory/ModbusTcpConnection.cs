using PumpSupervisor.Domain.Models;

namespace PumpSupervisor.Infrastructure.Modbus.Factory
{
    using Microsoft.Extensions.Logging;
    using NModbus;
    using System.Net.Sockets;

    namespace PumpSupervisor.Infrastructure.Modbus.Factory
    {
        public class ModbusTcpConnection : IModbusConnection
        {
            private readonly ModbusConnectionConfig _config;
            private readonly ILogger<ModbusTcpConnection> _logger;
            private TcpClient? _tcpClient;
            private IModbusMaster? _modbusMaster;
            private readonly SemaphoreSlim _connectionLock = new(1, 1);

            public string ConnectionId => _config.Id;
            public bool IsConnected => _tcpClient?.Connected ?? false;

            public ModbusTcpConnection(
                ModbusConnectionConfig config,
                ILogger<ModbusTcpConnection> logger)
            {
                _config = config;
                _logger = logger;
            }

            public async Task ConnectAsync(CancellationToken cancellationToken = default)
            {
                await _connectionLock.WaitAsync(cancellationToken);
                try
                {
                    if (IsConnected)
                    {
                        _logger.LogDebug("连接 {ConnectionId} 已建立", ConnectionId);
                        return;
                    }

                    _logger.LogInformation("正在连接到 {Host}:{Port}...",
                        _config.Connection.Host, _config.Connection.Port);

                    _tcpClient = new TcpClient();
                    await _tcpClient.ConnectAsync(
                        _config.Connection.Host!,
                        _config.Connection.Port!.Value,
                        cancellationToken);

                    var factory = new ModbusFactory();
                    _modbusMaster = factory.CreateMaster(_tcpClient);

                    // 连接后暂停
                    var pauseMs = ParseDuration(_config.Connection.PauseAfterConnect);
                    if (pauseMs > 0)
                    {
                        await Task.Delay(pauseMs, cancellationToken);
                    }

                    _logger.LogInformation("连接 {ConnectionId} 建立成功", ConnectionId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "连接 {ConnectionId} 失败", ConnectionId);
                    await DisconnectAsync(cancellationToken);
                    throw;
                }
                finally
                {
                    _connectionLock.Release();
                }
            }

            public async Task DisconnectAsync(CancellationToken cancellationToken = default)
            {
                await _connectionLock.WaitAsync(cancellationToken);
                try
                {
                    _modbusMaster?.Dispose();
                    _modbusMaster = null;

                    _tcpClient?.Close();
                    _tcpClient?.Dispose();
                    _tcpClient = null;

                    _logger.LogInformation("连接 {ConnectionId} 已断开", ConnectionId);
                }
                finally
                {
                    _connectionLock.Release();
                }
            }

            public async Task<ushort[]> ReadHoldingRegistersAsync(
                byte slaveId,
                ushort startAddress,
                ushort count,
                CancellationToken cancellationToken = default)
            {
                await EnsureConnectedAsync(cancellationToken);

                try
                {
                    return await Task.Run(() =>
                        _modbusMaster!.ReadHoldingRegisters(slaveId, startAddress, count),
                        cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "读取保持寄存器失败: SlaveId={SlaveId}, Start={Start}, Count={Count}",
                        slaveId, startAddress, count);

                    if (_config.Connection.CloseConnectionAfterGathering)
                    {
                        await DisconnectAsync(cancellationToken);
                    }
                    throw;
                }
            }

            public async Task<ushort[]> ReadInputRegistersAsync(
                byte slaveId,
                ushort startAddress,
                ushort count,
                CancellationToken cancellationToken = default)
            {
                await EnsureConnectedAsync(cancellationToken);

                try
                {
                    return await Task.Run(() =>
                        _modbusMaster!.ReadInputRegisters(slaveId, startAddress, count),
                        cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "读取输入寄存器失败: SlaveId={SlaveId}, Start={Start}, Count={Count}",
                        slaveId, startAddress, count);

                    if (_config.Connection.CloseConnectionAfterGathering)
                    {
                        await DisconnectAsync(cancellationToken);
                    }
                    throw;
                }
            }

            public async Task<bool[]> ReadCoilsAsync(
                byte slaveId,
                ushort startAddress,
                ushort count,
                CancellationToken cancellationToken = default)
            {
                await EnsureConnectedAsync(cancellationToken);

                try
                {
                    return await Task.Run(() =>
                        _modbusMaster!.ReadCoils(slaveId, startAddress, count),
                        cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "读取线圈失败");
                    throw;
                }
            }

            public async Task<bool[]> ReadInputsAsync(
                byte slaveId,
                ushort startAddress,
                ushort count,
                CancellationToken cancellationToken = default)
            {
                await EnsureConnectedAsync(cancellationToken);

                try
                {
                    return await Task.Run(() =>
                        _modbusMaster!.ReadInputs(slaveId, startAddress, count),
                        cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "读取离散输入失败");
                    throw;
                }
            }

            public async Task WriteSingleRegisterAsync(
                byte slaveId,
                ushort address,
                ushort value,
                CancellationToken cancellationToken = default)
            {
                await EnsureConnectedAsync(cancellationToken);

                try
                {
                    await Task.Run(() =>
                        _modbusMaster!.WriteSingleRegister(slaveId, address, value),
                        cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "写入单个寄存器失败");
                    throw;
                }
            }

            public async Task WriteMultipleRegistersAsync(
                byte slaveId,
                ushort startAddress,
                ushort[] values,
                CancellationToken cancellationToken = default)
            {
                await EnsureConnectedAsync(cancellationToken);

                try
                {
                    await Task.Run(() =>
                        _modbusMaster!.WriteMultipleRegisters(slaveId, startAddress, values),
                        cancellationToken);

                    _logger.LogDebug("写入多个寄存器成功: Start={Start}, Count={Count}",
                        startAddress, values.Length);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "写入多个寄存器失败");
                    throw;
                }
            }

            public async Task WriteSingleCoilAsync(
                byte slaveId,
                ushort address,
                bool value,
                CancellationToken cancellationToken = default)
            {
                await EnsureConnectedAsync(cancellationToken);

                try
                {
                    await Task.Run(() =>
                        _modbusMaster!.WriteSingleCoil(slaveId, address, value),
                        cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "写入单个线圈失败");
                    throw;
                }
            }

            private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
            {
                if (!IsConnected)
                {
                    await ConnectAsync(cancellationToken);
                }
            }

            private int ParseDuration(string duration)
            {
                if (string.IsNullOrEmpty(duration)) return 0;

                var value = int.Parse(duration.TrimEnd('m', 's'));
                return duration.EndsWith("ms") ? value : value * 1000;
            }

            public async ValueTask DisposeAsync()
            {
                await DisconnectAsync();
                _connectionLock?.Dispose();
            }
        }
    }
}