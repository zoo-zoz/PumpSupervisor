using InfluxDB.Client;
using InfluxDB.Client.Api.Domain;
using InfluxDB.Client.Writes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PumpSupervisor.Domain.Models;

namespace PumpSupervisor.Infrastructure.Storage.InfluxDb
{
    public interface IInfluxDbService
    {
        Task WriteDataBatchAsync(ModbusDataBatch batch, CancellationToken cancellationToken = default);

        Task<List<ModbusDataPoint>> QueryDataAsync(
            string deviceId,
            string parameterCode,
            DateTime start,
            DateTime end,
            CancellationToken cancellationToken = default);
    }

    public class InfluxDbService : IInfluxDbService, IHostedService, IAsyncDisposable
    {
        private readonly ILogger<InfluxDbService> _logger;
        private readonly IConfiguration _configuration;
        private InfluxDBClient? _client;
        private string _bucket = "";
        private string _org = "";
        private bool _isEnabled;

        public InfluxDbService(
            ILogger<InfluxDbService> logger,
            IConfiguration configuration)
        {
            _logger = logger;
            _configuration = configuration;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("InfluxDB Service 正在启动...");

            try
            {
                var url = _configuration["InfluxDb:Url"] ?? "http://localhost:8086";
                var token = _configuration["InfluxDb:Token"];
                _bucket = _configuration["InfluxDb:Bucket"] ?? "nbcb";
                _org = _configuration["InfluxDb:Org"] ?? "nbcb";

                _logger.LogInformation("InfluxDB 配置: Url={Url}, Bucket={Bucket}, Org={Org}",
                    url, _bucket, _org);

                if (string.IsNullOrEmpty(token))
                {
                    _logger.LogWarning("InfluxDB Token 未配置,InfluxDB 功能将被禁用");
                    _isEnabled = false;
                    return Task.CompletedTask;
                }

                _client = new InfluxDBClient(url, token);
                _isEnabled = true;

                _logger.LogInformation("✓ InfluxDB Service 启动完成");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "✗ InfluxDB Service 启动失败,将禁用 InfluxDB 功能");
                _isEnabled = false;
            }

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("InfluxDB Service 正在停止...");
            return Task.CompletedTask;
        }

        public async Task WriteDataBatchAsync(ModbusDataBatch batch, CancellationToken cancellationToken = default)
        {
            if (!_isEnabled || _client == null)
            {
                _logger.LogWarning("InfluxDB 未启用,跳过写入: {DeviceId}", batch.DeviceId);
                return;
            }

            _logger.LogDebug("准备写入 InfluxDB: Connection={Conn}, Device={Dev}, 数据点数={Count}",
                batch.ConnectionId, batch.DeviceId, batch.DataPoints.Count);

            try
            {
                var writeApi = _client.GetWriteApiAsync();
                var points = new List<PointData>();

                foreach (var dataPoint in batch.DataPoints)
                {
                    try
                    {
                        var pointList = CreatePointsForDataType(dataPoint);
                        if (pointList != null && pointList.Count > 0)
                        {
                            points.AddRange(pointList);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "❌ 构建数据点失败: {ParamCode}", dataPoint.ParameterCode);
                    }
                }

                if (points.Count > 0)
                {
                    await writeApi.WritePointsAsync(points, _bucket, _org, cancellationToken);

                    _logger.LogDebug("✓ 写入 {Count} 个数据点到 InfluxDB: {DeviceId}",
                        points.Count, batch.DeviceId);
                }
                else
                {
                    _logger.LogWarning("⚠ 没有有效数据点可写入 InfluxDB: {DeviceId}", batch.DeviceId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ 写入 InfluxDB 失败: {ConnectionId}/{DeviceId}",
                    batch.ConnectionId, batch.DeviceId);
            }
        }

        /// <summary>
        /// 根据数据类型创建 PointData 列表（统一使用 value 字段）
        /// </summary>
        private List<PointData>? CreatePointsForDataType(ModbusDataPoint dataPoint)
        {
            var points = new List<PointData>();
            var parsedValue = dataPoint.ParsedValue;
            var rawValue = dataPoint.RawValue;

            try
            {
                if (parsedValue is Dictionary<string, bool> bitDict)
                {
                    if (bitDict.Count == 0) return null;

                    foreach (var kvp in bitDict)
                    {
                        // 为每个位创建独立的数据点
                        var bitParameterCode = $"{dataPoint.ParameterCode}_{kvp.Key}";

                        var point = PointData
                            .Measurement("nbcb_collect_pump_sensor_data")
                            .Tag("connection_id", dataPoint.ConnectionId)
                            .Tag("device_id", dataPoint.DeviceId)
                            .Tag("parameter_code", bitParameterCode)
                            .Timestamp(dataPoint.Timestamp, WritePrecision.Ms)
                            .Field("value", kvp.Value ? 1.0 : 0.0);

                        points.Add(point);
                    }

                    return points;
                }

                // 提取原始数值存储
                if (parsedValue is string strValue)
                {
                    double value;

                    switch (rawValue)
                    {
                        case ushort us: value = us; break;
                        case int i: value = i; break;
                        case uint ui: value = ui; break;
                        case short s: value = s; break;
                        case byte b: value = b; break;
                        default:
                            _logger.LogWarning("枚举类型无法提取数值: {Code}, RawType={Type}",
                                dataPoint.ParameterCode, rawValue?.GetType().Name);
                            return null;
                    }

                    var point = PointData
                        .Measurement("nbcb_collect_pump_sensor_data")
                        .Tag("connection_id", dataPoint.ConnectionId)
                        .Tag("device_id", dataPoint.DeviceId)
                        .Tag("parameter_code", dataPoint.ParameterCode)
                        .Timestamp(dataPoint.Timestamp, WritePrecision.Ms)
                        .Field("value", value);

                    points.Add(point);
                    return points;
                }

                // 3️⃣ 普通数值类型：统一转换为 double
                double numericValue;

                switch (parsedValue)
                {
                    case double d: numericValue = d; break;
                    case float f: numericValue = f; break;
                    case int i: numericValue = i; break;
                    case uint ui: numericValue = ui; break;
                    case long l: numericValue = l; break;
                    case ulong ul: numericValue = ul; break;
                    case short s: numericValue = s; break;
                    case ushort us: numericValue = us; break;
                    case byte b: numericValue = b; break;
                    case bool b: numericValue = b ? 1.0 : 0.0; break;
                    default:
                        // 尝试从 rawValue 获取
                        if (rawValue == null)
                        {
                            _logger.LogWarning("无效数据: {Code}, ParsedValue 和 RawValue 均为空",
                                dataPoint.ParameterCode);
                            return null;
                        }

                        switch (rawValue)
                        {
                            case double d: numericValue = d; break;
                            case float f: numericValue = f; break;
                            case int i: numericValue = i; break;
                            case uint ui: numericValue = ui; break;
                            case long l: numericValue = l; break;
                            case ulong ul: numericValue = ul; break;
                            case short s: numericValue = s; break;
                            case ushort us: numericValue = us; break;
                            case byte b: numericValue = b; break;
                            default:
                                _logger.LogWarning("无法处理的数据类型: {Code}, ParsedType={PType}, RawType={RType}",
                                    dataPoint.ParameterCode,
                                    parsedValue?.GetType().Name,
                                    rawValue.GetType().Name);
                                return null;
                        }
                        break;
                }

                // 创建统一的数据点
                var standardPoint = PointData
                    .Measurement("nbcb_collect_pump_sensor_data")
                    .Tag("connection_id", dataPoint.ConnectionId)
                    .Tag("device_id", dataPoint.DeviceId)
                    .Tag("parameter_code", dataPoint.ParameterCode)
                    .Timestamp(dataPoint.Timestamp, WritePrecision.Ms)
                    .Field("value", numericValue);

                points.Add(standardPoint);
                return points;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "创建数据点失败: {Code}", dataPoint.ParameterCode);
                return null;
            }
        }

        public async Task<List<ModbusDataPoint>> QueryDataAsync(
            string deviceId,
            string parameterCode,
            DateTime start,
            DateTime end,
            CancellationToken cancellationToken = default)
        {
            if (!_isEnabled || _client == null)
            {
                _logger.LogWarning("InfluxDB 未启用,无法查询数据");
                return new List<ModbusDataPoint>();
            }

            var queryApi = _client.GetQueryApi();

            var flux = $@"
                from(bucket: ""{_bucket}"")
                |> range(start: {start:O}, stop: {end:O})
                |> filter(fn: (r) => r[""_measurement""] == ""nbcb_collect_pump_sensor_data"")
                |> filter(fn: (r) => r[""device_id""] == ""{deviceId}"")
                |> filter(fn: (r) => r[""parameter_code""] == ""{parameterCode}"")
                |> filter(fn: (r) => r[""_field""] == ""value"")
            ";

            var tables = await queryApi.QueryAsync(flux, _org, cancellationToken);
            var dataPoints = new List<ModbusDataPoint>();

            foreach (var table in tables)
            {
                foreach (var record in table.Records)
                {
                    var dataPoint = new ModbusDataPoint
                    {
                        ConnectionId = record.GetValueByKey("connection_id")?.ToString() ?? "",
                        DeviceId = record.GetValueByKey("device_id")?.ToString() ?? "",
                        ParameterCode = record.GetValueByKey("parameter_code")?.ToString() ?? "",
                        ParsedValue = record.GetValue(),
                        Timestamp = record.GetTime()?.ToDateTimeUtc() ?? DateTime.UtcNow
                    };

                    dataPoints.Add(dataPoint);
                }
            }

            return dataPoints;
        }

        public async ValueTask DisposeAsync()
        {
            _client?.Dispose();
            await Task.CompletedTask;
        }
    }
}