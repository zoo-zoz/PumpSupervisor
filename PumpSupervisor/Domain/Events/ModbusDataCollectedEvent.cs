using PumpSupervisor.Domain.Models;

namespace PumpSupervisor.Domain.Events
{
    // 数据采集完成事件
    public record ModbusDataCollectedEvent(
        string ConnectionId,
        string DeviceId,
        Dictionary<int, ushort> RegisterData,
        DateTime Timestamp
    );

    // 数据写入Slave完成事件
    public record DataWrittenToSlaveEvent(
        string ConnectionId,
        string DeviceId,
        ushort[] Data,
        DateTime Timestamp
    );

    // 数据解析完成事件
    public record DataParsedEvent(
        ModbusDataBatch DataBatch
    );

    /// <summary>
    /// 参数值变化事件
    /// </summary>
    public record ParameterValueChangedEvent(
        string ConnectionId,
        string DeviceId,
        string ParameterCode,
        string ParameterName,
        object OldValue,
        object NewValue,
        string? Unit,
        DateTime Timestamp,
        ModbusDataPoint DataPoint
    );

    // 数据存储完成事件
    public record DataStoredEvent(
        string ConnectionId,
        string DeviceId,
        int DataPointCount,
        DateTime StoredAt,
        ModbusDataBatch DataBatch
    );

    // MQTT发布完成事件
    public record DataPublishedToMqttEvent(
        string Topic,
        string ConnectionId,
        string DeviceId,
        DateTime Timestamp
    );
}