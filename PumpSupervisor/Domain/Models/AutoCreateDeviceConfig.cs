using System.Text.Json.Serialization;

namespace PumpSupervisor.Domain.Models
{
    public class AutoCreateDeviceConfig
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

        [JsonPropertyName("byte_order")]
        public string ByteOrder { get; set; } = "ABCD";
    }
}