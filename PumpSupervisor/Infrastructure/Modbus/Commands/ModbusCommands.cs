namespace PumpSupervisor.Infrastructure.Modbus.Commands
{
    // 读取命令（低优先级）
    public record ReadModbusDataCommand(
        string ConnectionId,
        string DeviceId,
        int Priority = 1  // 默认优先级为1
    );

    // 写入命令（高优先级）
    public record WriteModbusDataCommand(
        string ConnectionId,
        string DeviceId,
        int StartAddress,
        ushort[] Values,
        int Priority = 10  // 写入操作默认高优先级
    );

    // 按需读取命令
    public record ReadOnDemandCommand(
        string ConnectionId,
        string DeviceId,
        int Priority = 2
    );

    // 命令结果
    public record ModbusCommandResult(
        bool Success,
        string Message = "",
        object? Data = null
    )
    {
        public Dictionary<int, ushort>? GetRegisterData()
        {
            return Data as Dictionary<int, ushort>;
        }
    }
}