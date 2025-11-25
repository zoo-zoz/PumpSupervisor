using System.Text.Json.Serialization;

namespace PumpSupervisor.Domain.Models
{
    public class ModbusConnectionConfig
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; }

        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty; // TCP or RTU

        [JsonPropertyName("register_type")]
        public string RegisterType { get; set; } = string.Empty;

        [JsonPropertyName("connection")]
        public ConnectionSettings Connection { get; set; } = new();

        [JsonPropertyName("slave_id")]
        public int SlaveId { get; set; }

        [JsonPropertyName("slave_port")]
        public int SlavePort { get; set; }

        [JsonPropertyName("poll_interval")]
        public string PollInterval { get; set; } = "1s";

        /// <summary>
        /// 连续采集模式的最小轮询间隔（毫秒）
        /// 0 = 无延迟（立即进行下一次采集）
        /// 默认 10ms
        /// </summary>
        [JsonPropertyName("min_poll_interval")]
        public int? MinPollInterval { get; set; } = 10;

        [JsonPropertyName("byte_order")]
        public string ByteOrder { get; set; } = "ABCD";

        [JsonPropertyName("devices")]
        public List<DeviceConfig> Devices { get; set; } = new();
    }

    public class ConnectionSettings
    {
        // TCP Settings
        [JsonPropertyName("host")]
        public string? Host { get; set; }

        [JsonPropertyName("port")]
        public int? Port { get; set; }

        // RTU Settings
        [JsonPropertyName("serial_port")]
        public string? SerialPort { get; set; }

        [JsonPropertyName("baud_rate")]
        public int? BaudRate { get; set; }

        [JsonPropertyName("data_bits")]
        public int? DataBits { get; set; }

        [JsonPropertyName("parity")]
        public string? Parity { get; set; }

        [JsonPropertyName("stop_bits")]
        public int? StopBits { get; set; }

        // Common Settings
        [JsonPropertyName("timeout")]
        public string Timeout { get; set; } = "5s";

        [JsonPropertyName("close_connection_after_gathering")]
        public bool CloseConnectionAfterGathering { get; set; }

        [JsonPropertyName("pause_after_connect")]
        public string PauseAfterConnect { get; set; } = "20ms";
    }

    public class DeviceConfig
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; }

        /// <summary>
        /// 外部配置文件路径 (相对于 exe 根目录)
        /// 如果配置了 path,则从外部文件加载 poll_mode, read_blocks, parameters
        /// </summary>
        [JsonPropertyName("path")]
        public string? Path { get; set; }

        [JsonPropertyName("poll_mode")]
        public string PollMode { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        [JsonPropertyName("read_blocks")]
        public List<ReadBlock> ReadBlocks { get; set; } = new();

        [JsonPropertyName("parameters")]
        public List<ParameterConfig> Parameters { get; set; } = new();
    }

    public class ReadBlock
    {
        [JsonPropertyName("start")]
        public int Start { get; set; }

        [JsonPropertyName("count")]
        public int Count { get; set; }

        [JsonPropertyName("comment")]
        public string Comment { get; set; } = string.Empty;
    }

    public class ParameterConfig
    {
        [JsonPropertyName("code")]
        public string Code { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; }

        [JsonPropertyName("address")]
        public List<int> Address { get; set; } = new();

        [JsonPropertyName("data_type")]
        public string DataType { get; set; } = string.Empty;

        [JsonPropertyName("unit")]
        public string Unit { get; set; } = string.Empty;

        [JsonPropertyName("scale")]
        public double Scale { get; set; } = 1.0;

        [JsonPropertyName("offset")]
        public double Offset { get; set; }

        [JsonPropertyName("precision")]
        public int Precision { get; set; }

        [JsonPropertyName("on_change")]
        public bool OnChange { get; set; }

        [JsonPropertyName("bit_map")]
        public Dictionary<string, BitMapItem>? BitMap { get; set; }

        [JsonPropertyName("enum_map")]
        public Dictionary<string, string>? EnumMap { get; set; }

        [JsonPropertyName("range")]
        public RangeConfig? Range { get; set; }
    }

    public class BitMapItem
    {
        [JsonPropertyName("code")]
        public string Code { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;
    }

    public class RangeConfig
    {
        [JsonPropertyName("min")]
        public double Min { get; set; }

        [JsonPropertyName("max")]
        public double Max { get; set; }
    }
}