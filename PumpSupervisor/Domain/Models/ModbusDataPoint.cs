namespace PumpSupervisor.Domain.Models
{
    public class ModbusDataPoint
    {
        public string ConnectionId { get; set; } = string.Empty;
        public string DeviceId { get; set; } = string.Empty;
        public string ParameterCode { get; set; } = string.Empty;
        public string ParameterName { get; set; } = string.Empty;
        public object RawValue { get; set; } = new object();
        public object ParsedValue { get; set; } = new object();
        public string Unit { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new();
    }

    public class ModbusDataBatch
    {
        public string ConnectionId { get; set; } = string.Empty;
        public string DeviceId { get; set; } = string.Empty;
        public List<ModbusDataPoint> DataPoints { get; set; } = new();
        public DateTime Timestamp { get; set; }
    }
}