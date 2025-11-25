using Microsoft.Extensions.Logging;
using NModbus;
using NModbus.IO;
using PumpSupervisor.Domain.Models;
using System.IO.Ports;

namespace PumpSupervisor.Infrastructure.Modbus.Factory
{
    /// <summary>
    /// SerialPort 适配器，实现 IStreamResource 接口
    /// </summary>
    public class SerialPortStreamResource : IStreamResource
    {
        private readonly SerialPort _serialPort;

        public SerialPortStreamResource(SerialPort serialPort)
        {
            _serialPort = serialPort ?? throw new ArgumentNullException(nameof(serialPort));
        }

        public Stream Stream => _serialPort.BaseStream;

        public int InfiniteTimeout => SerialPort.InfiniteTimeout;

        public int ReadTimeout
        {
            get => _serialPort.ReadTimeout;
            set => _serialPort.ReadTimeout = value;
        }

        public int WriteTimeout
        {
            get => _serialPort.WriteTimeout;
            set => _serialPort.WriteTimeout = value;
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            return _serialPort.BaseStream.Read(buffer, offset, count);
        }

        public void Write(byte[] buffer, int offset, int count)
        {
            _serialPort.BaseStream.Write(buffer, offset, count);
            _serialPort.BaseStream.Flush();
        }

        public void DiscardInBuffer()
        {
            _serialPort.DiscardInBuffer();
        }

        public void Dispose()
        {
        }
    }

    public class ModbusRtuConnection : IModbusConnection
    {
        private readonly ModbusConnectionConfig _config;
        private readonly ILogger<ModbusRtuConnection> _logger;
        private SerialPort? _serialPort;
        private IModbusMaster? _modbusMaster;
        private SerialPortStreamResource? _streamResource;
        private readonly SemaphoreSlim _connectionLock = new(1, 1);

        public string ConnectionId => _config.Id;
        public bool IsConnected => _serialPort?.IsOpen ?? false;

        public ModbusRtuConnection(
            ModbusConnectionConfig config,
            ILogger<ModbusRtuConnection> logger)
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
                    _logger.LogDebug("串口 {Port} 已打开", _config.Connection.SerialPort);
                    return;
                }

                _logger.LogInformation("正在打开串口 {Port}...", _config.Connection.SerialPort);

                _serialPort = new SerialPort
                {
                    PortName = _config.Connection.SerialPort!,
                    BaudRate = _config.Connection.BaudRate!.Value,
                    DataBits = _config.Connection.DataBits!.Value,
                    Parity = ParseParity(_config.Connection.Parity!),
                    StopBits = ParseStopBits(_config.Connection.StopBits!.Value),
                    ReadTimeout = 1000,
                    WriteTimeout = 1000
                };

                _serialPort.Open();

                // 创建 IStreamResource 适配器
                _streamResource = new SerialPortStreamResource(_serialPort);

                // 使用适配器创建 Modbus Master
                var factory = new ModbusFactory();
                _modbusMaster = factory.CreateRtuMaster(_streamResource);

                // 配置传输层
                _modbusMaster.Transport.ReadTimeout = 1000;
                _modbusMaster.Transport.WriteTimeout = 1000;
                _modbusMaster.Transport.Retries = 3;

                // 连接后暂停
                var pauseMs = ParseDuration(_config.Connection.PauseAfterConnect);
                if (pauseMs > 0)
                {
                    await Task.Delay(pauseMs, cancellationToken);
                }

                _logger.LogInformation("串口 {Port} 打开成功", _config.Connection.SerialPort);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "打开串口 {Port} 失败", _config.Connection.SerialPort);
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

                _streamResource?.Dispose();
                _streamResource = null;

                if (_serialPort?.IsOpen == true)
                {
                    _serialPort.Close();
                }
                _serialPort?.Dispose();
                _serialPort = null;

                _logger.LogInformation("串口连接 {ConnectionId} 已断开", ConnectionId);
            }
            finally
            {
                _connectionLock.Release();
            }
        }

        public async Task<ushort[]> ReadHoldingRegistersAsync(
            byte slaveId, ushort startAddress, ushort count,
            CancellationToken cancellationToken = default)
        {
            await EnsureConnectedAsync(cancellationToken);
            return await Task.Run(() =>
                _modbusMaster!.ReadHoldingRegisters(slaveId, startAddress, count),
                cancellationToken);
        }

        public async Task<ushort[]> ReadInputRegistersAsync(
            byte slaveId, ushort startAddress, ushort count,
            CancellationToken cancellationToken = default)
        {
            await EnsureConnectedAsync(cancellationToken);
            return await Task.Run(() =>
                _modbusMaster!.ReadInputRegisters(slaveId, startAddress, count),
                cancellationToken);
        }

        public async Task<bool[]> ReadCoilsAsync(
            byte slaveId, ushort startAddress, ushort count,
            CancellationToken cancellationToken = default)
        {
            await EnsureConnectedAsync(cancellationToken);
            return await Task.Run(() =>
                _modbusMaster!.ReadCoils(slaveId, startAddress, count),
                cancellationToken);
        }

        public async Task<bool[]> ReadInputsAsync(
            byte slaveId, ushort startAddress, ushort count,
            CancellationToken cancellationToken = default)
        {
            await EnsureConnectedAsync(cancellationToken);
            return await Task.Run(() =>
                _modbusMaster!.ReadInputs(slaveId, startAddress, count),
                cancellationToken);
        }

        public async Task WriteSingleRegisterAsync(
            byte slaveId, ushort address, ushort value,
            CancellationToken cancellationToken = default)
        {
            await EnsureConnectedAsync(cancellationToken);
            await Task.Run(() =>
                _modbusMaster!.WriteSingleRegister(slaveId, address, value),
                cancellationToken);
        }

        public async Task WriteMultipleRegistersAsync(
            byte slaveId, ushort startAddress, ushort[] values,
            CancellationToken cancellationToken = default)
        {
            await EnsureConnectedAsync(cancellationToken);
            await Task.Run(() =>
                _modbusMaster!.WriteMultipleRegisters(slaveId, startAddress, values),
                cancellationToken);
        }

        public async Task WriteSingleCoilAsync(
            byte slaveId, ushort address, bool value,
            CancellationToken cancellationToken = default)
        {
            await EnsureConnectedAsync(cancellationToken);
            await Task.Run(() =>
                _modbusMaster!.WriteSingleCoil(slaveId, address, value),
                cancellationToken);
        }

        private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
        {
            if (!IsConnected)
            {
                await ConnectAsync(cancellationToken);
            }
        }

        private Parity ParseParity(string parity) => parity.ToUpper() switch
        {
            "NONE" => Parity.None,
            "ODD" => Parity.Odd,
            "EVEN" => Parity.Even,
            "MARK" => Parity.Mark,
            "SPACE" => Parity.Space,
            _ => Parity.None
        };

        private StopBits ParseStopBits(int bits) => bits switch
        {
            1 => StopBits.One,
            2 => StopBits.Two,
            _ => StopBits.One
        };

        private int ParseDuration(string? duration)
        {
            if (string.IsNullOrEmpty(duration)) return 0;

            var numericPart = duration.TrimEnd('m', 's');
            if (!int.TryParse(numericPart, out var value))
                return 0;

            return duration.EndsWith("ms") ? value : value * 1000;
        }

        public async ValueTask DisposeAsync()
        {
            await DisconnectAsync();
            _connectionLock?.Dispose();
        }
    }
}