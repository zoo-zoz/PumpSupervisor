using PumpSupervisor.Domain.Models;

namespace PumpSupervisor.Infrastructure.Modbus.Factory
{
    public interface IModbusConnectionFactory
    {
        IModbusConnection CreateConnection(ModbusConnectionConfig config);
    }

    public interface IModbusConnection : IAsyncDisposable
    {
        string ConnectionId { get; }
        bool IsConnected { get; }

        Task ConnectAsync(CancellationToken cancellationToken = default);

        Task DisconnectAsync(CancellationToken cancellationToken = default);

        // 读操作
        Task<ushort[]> ReadHoldingRegistersAsync(
            byte slaveId,
            ushort startAddress,
            ushort count,
            CancellationToken cancellationToken = default);

        Task<ushort[]> ReadInputRegistersAsync(
            byte slaveId,
            ushort startAddress,
            ushort count,
            CancellationToken cancellationToken = default);

        Task<bool[]> ReadCoilsAsync(
            byte slaveId,
            ushort startAddress,
            ushort count,
            CancellationToken cancellationToken = default);

        Task<bool[]> ReadInputsAsync(
            byte slaveId,
            ushort startAddress,
            ushort count,
            CancellationToken cancellationToken = default);

        // 写操作
        Task WriteSingleRegisterAsync(
            byte slaveId,
            ushort address,
            ushort value,
            CancellationToken cancellationToken = default);

        Task WriteMultipleRegistersAsync(
            byte slaveId,
            ushort startAddress,
            ushort[] values,
            CancellationToken cancellationToken = default);

        Task WriteSingleCoilAsync(
            byte slaveId,
            ushort address,
            bool value,
            CancellationToken cancellationToken = default);
    }
}