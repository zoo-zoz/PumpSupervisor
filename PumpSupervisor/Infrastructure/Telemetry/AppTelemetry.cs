using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace PumpSupervisor.Infrastructure.Telemetry
{
    /// <summary>
    /// 应用程序遥测指标 - 统一管理所有自定义指标
    /// </summary>
    public static class AppTelemetry
    {
        // 定义应用程序级别的 ActivitySource 和 Meter
        public static readonly ActivitySource ActivitySource = new("PumpSupervisor");

        public static readonly Meter Meter = new("PumpSupervisor");

        /// <summary>
        /// 业务指标定义
        /// </summary>
        public static class Metrics
        {
            // Modbus 相关指标
            public static readonly Counter<long> ModbusReadCounter = Meter.CreateCounter<long>(
                "modbus.reads.total",
                description: "Modbus读取总次数");

            public static readonly Counter<long> ModbusReadErrorCounter = Meter.CreateCounter<long>(
                "modbus.reads.errors",
                description: "Modbus读取错误次数");

            public static readonly Histogram<double> ModbusReadDuration = Meter.CreateHistogram<double>(
                "modbus.read.duration",
                unit: "ms",
                description: "Modbus读取耗时");

            // 数据处理指标
            public static readonly Counter<long> DataPointsProcessed = Meter.CreateCounter<long>(
                "data.points.processed",
                description: "处理的数据点总数");

            // InfluxDB 相关指标
            public static readonly Counter<long> InfluxDbWriteCounter = Meter.CreateCounter<long>(
                "influxdb.writes.total",
                description: "InfluxDB写入总次数");

            public static readonly Counter<long> InfluxDbWriteErrorCounter = Meter.CreateCounter<long>(
                "influxdb.writes.errors",
                description: "InfluxDB写入错误次数");

            public static readonly Histogram<double> InfluxDbWriteDuration = Meter.CreateHistogram<double>(
                "influxdb.write.duration",
                unit: "ms",
                description: "InfluxDB写入耗时");

            // MQTT 相关指标
            public static readonly Counter<long> MqttPublishCounter = Meter.CreateCounter<long>(
                "mqtt.publishes.total",
                description: "MQTT发布总次数");

            public static readonly Counter<long> MqttPublishErrorCounter = Meter.CreateCounter<long>(
                "mqtt.publishes.errors",
                description: "MQTT发布错误次数");

            // 事件处理指标
            public static readonly Histogram<double> EventProcessingDuration = Meter.CreateHistogram<double>(
                "event.processing.duration",
                unit: "ms",
                description: "事件处理耗时");

            // 连接状态指标
            public static readonly ObservableGauge<int> ActiveConnections = Meter.CreateObservableGauge(
                "modbus.connections.active",
                () => GetActiveConnectionsCount(),
                description: "活动的Modbus连接数");

            public static readonly ObservableGauge<int> SlaveInstancesCount = Meter.CreateObservableGauge(
                "modbus.slave.instances",
                () => GetSlaveInstancesCount(),
                description: "虚拟Slave实例数量");

            // 内部状态存储
            private static int _activeConnectionsCount;

            private static int _slaveInstancesCount;

            public static void SetActiveConnectionsCount(int count) => _activeConnectionsCount = count;

            public static void SetSlaveInstancesCount(int count) => _slaveInstancesCount = count;

            private static int GetActiveConnectionsCount() => _activeConnectionsCount;

            private static int GetSlaveInstancesCount() => _slaveInstancesCount;
        }
    }
}