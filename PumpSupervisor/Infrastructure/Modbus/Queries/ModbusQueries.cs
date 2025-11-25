namespace PumpSupervisor.Infrastructure.Modbus.Queries
{
    // 查询连接状态
    public record GetConnectionStatusQuery(string ConnectionId);

    public record ConnectionStatusResult(
        string ConnectionId,
        bool IsConnected,
        DateTime? LastSuccessfulRead,
        DateTime? LastError,
        string? ErrorMessage
    );

    // 查询设备数据
    public record GetDeviceDataQuery(
        string ConnectionId,
        string DeviceId
    );

    public record DeviceDataResult(
        string DeviceId,
        Dictionary<string, object> Parameters,
        DateTime Timestamp
    );

    // 查询参数历史值
    public record GetParameterHistoryQuery(
        string ConnectionId,
        string DeviceId,
        string ParameterCode,
        DateTime StartTime,
        DateTime EndTime
    );
}