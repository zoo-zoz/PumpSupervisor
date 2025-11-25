using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NModbus;
using PumpSupervisor.Domain.Models;
using PumpSupervisor.Infrastructure.Telemetry;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace PumpSupervisor.Infrastructure.Storage.ModbusSlave
{
    /// <summary>
    /// Modbus TCP Slave 管理服务 - 为每个连接创建独立的虚拟 Slave
    /// </summary>
    public class ModbusTcpSlaveService : BackgroundService
    {
        private readonly ILogger<ModbusTcpSlaveService> _logger;
        private readonly IConfiguration _configuration;
        private readonly ConcurrentDictionary<string, ModbusTcpSlaveInstance> _slaveInstances = new();
        private readonly TaskCompletionSource<bool> _initializationComplete = new();

        // 自动分配端口的起始值
        private const int AUTO_PORT_START = 60000;

        private int _nextAutoPort = AUTO_PORT_START;

        public ModbusTcpSlaveService(
            ILogger<ModbusTcpSlaveService> logger,
            IConfiguration configuration)
        {
            _logger = logger;
            _configuration = configuration;
        }

        /// <summary>
        /// 等待服务初始化完成
        /// </summary>
        public async Task WaitForInitializationAsync(CancellationToken cancellationToken = default)
        {
            await _initializationComplete.Task.WaitAsync(cancellationToken);
        }

        /// <summary>
        /// 检查服务是否已初始化
        /// </summary>
        public bool IsInitialized => _initializationComplete.Task.IsCompletedSuccessfully;

        /// <summary>
        /// 检查指定连接是否有 Slave 实例
        /// </summary>
        public bool HasSlaveInstance(string connectionId)
        {
            return _slaveInstances.ContainsKey(connectionId);
        }

        public override async Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("⚙️ 正在启动 Modbus TCP Slave 管理服务...");

            try
            {
                var configs = await LoadConnectionConfigsAsync();

                _logger.LogInformation("📋 加载配置完成，连接总数: {Total}", configs.Count);

                var enabledConfigs = configs.Where(c => c.Enabled).ToList();

                if (enabledConfigs.Count == 0)
                {
                    _logger.LogWarning("⚠️ 未找到启用的连接配置(虚拟共享Slave)");
                    _initializationComplete.SetResult(true);
                    await base.StartAsync(cancellationToken);
                    return;
                }

                _logger.LogInformation("✓ 找到 {Count} 个启用的连接配置(虚拟共享Slave):", enabledConfigs.Count);
                foreach (var cfg in enabledConfigs)
                {
                    _logger.LogInformation("  - {Id}: Type={Type}, SlaveId={SlaveId}, SlavePort={SlavePort}",
                        cfg.Id, cfg.Type, cfg.SlaveId, cfg.SlavePort);
                }

                var usedPorts = new HashSet<int>();
                int successCount = 0;

                foreach (var config in enabledConfigs)
                {
                    try
                    {
                        _logger.LogInformation("🔧 开始创建Slave: {ConnectionId}", config.Id);

                        // 确定要使用的端口
                        int slavePort = DetermineSlavePort(config, usedPorts);

                        if (slavePort <= 0)
                        {
                            _logger.LogError(
                                "❌ 无法为连接分配端口: ConnectionId={ConnectionId}",
                                config.Id);
                            continue;
                        }

                        _logger.LogDebug("  端口已确定: {Port}", slavePort);

                        // 检查端口是否已被使用
                        if (usedPorts.Contains(slavePort))
                        {
                            _logger.LogError(
                                "❌ 端口冲突: ConnectionId={ConnectionId}, Port={Port} 已被占用",
                                config.Id, slavePort);
                            continue;
                        }

                        _logger.LogDebug("  端口可用,开始创建实例...");

                        // 创建 Slave 实例
                        var slaveInstance = new ModbusTcpSlaveInstance(
                            config.Id,
                            slavePort,
                            (byte)config.SlaveId,
                            _logger);

                        _logger.LogDebug("  实例已创建,准备启动...");

                        await slaveInstance.StartAsync(cancellationToken);

                        _logger.LogDebug("  实例启动完成,添加到字典...");

                        _slaveInstances[config.Id] = slaveInstance;
                        usedPorts.Add(slavePort);
                        successCount++;

                        _logger.LogInformation(
                            "✅ Slave已创建: Id={ConnectionId,-30} | Type={Type,-4} | Port={Port,-5} | SlaveId={SlaveId}",
                            config.Id, config.Type, slavePort, config.SlaveId);

                        // 验证是否真的添加成功
                        if (_slaveInstances.ContainsKey(config.Id))
                        {
                            _logger.LogDebug("  ✓ 验证通过: 实例已在字典中");
                        }
                        else
                        {
                            _logger.LogError("  ❌ 验证失败: 实例不在字典中!");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "❌ 创建 Slave 失败: ConnectionId={ConnectionId}", config.Id);
                        _logger.LogError("异常详情: {Message}", ex.ToString());
                    }
                }

                _logger.LogInformation("📊 创建结果: 成功={Success}/{Total}", successCount, enabledConfigs.Count);
                _logger.LogInformation("📊 字典中实际实例数: {Count}", _slaveInstances.Count);

                if (successCount > 0)
                {
                    AppTelemetry.Metrics.SetSlaveInstancesCount(_slaveInstances.Count);
                    _logger.LogInformation("✓ ModbusTCP虚拟共享Slave服务启动完成");
                    _logger.LogInformation("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
                    _logger.LogInformation("虚拟共享Slave 实例列表:");
                    foreach (var instance in _slaveInstances.Values.OrderBy(i => i.Port))
                    {
                        _logger.LogInformation(
                            "  {ConnectionId,-30} → 127.0.0.1:{Port,-5} (SlaveId={SlaveId})",
                            instance.ConnectionId, instance.Port, instance.SlaveId);
                    }

                    _logger.LogInformation("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
                }
                else
                {
                    _logger.LogError("❌ 未成功创建任何 Slave 实例!");
                }

                // 标记初始化完成
                _initializationComplete.SetResult(true);
                _logger.LogInformation("✓ Slave 服务初始化完成信号已发出");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Slave 服务初始化失败");
                _logger.LogError("异常堆栈: {StackTrace}", ex.StackTrace);
                _initializationComplete.TrySetException(ex);
                throw;
            }

            await base.StartAsync(cancellationToken);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                _logger.LogInformation("Modbus TCP Slave 管理服务后台任务已启动");

                // 保持服务运行
                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Modbus TCP Slave 管理服务正在停止...");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Modbus TCP Slave 管理服务运行出错");
                throw;
            }
        }

        /// <summary>
        /// 确定 Slave 使用的端口
        /// </summary>
        private int DetermineSlavePort(ModbusConnectionConfig config, HashSet<int> usedPorts)
        {
            // 1. 如果配置了 slave_port 且有效,直接使用
            if (config.SlavePort > 0 && config.SlavePort <= 65535)
            {
                return config.SlavePort;
            }

            // 2. 自动分配端口
            _logger.LogInformation(
                "ConnectionId={ConnectionId} 未配置 slave_port，自动分配端口...",
                config.Id);

            // 尝试分配端口,最多尝试 1000 次
            for (int i = 0; i < 1000; i++)
            {
                int candidatePort = _nextAutoPort++;

                if (candidatePort > 65535)
                {
                    _nextAutoPort = AUTO_PORT_START; // 重新开始
                    candidatePort = _nextAutoPort++;
                }

                if (!usedPorts.Contains(candidatePort) && IsPortAvailable(candidatePort))
                {
                    _logger.LogInformation(
                        "✓ 为 ConnectionId={ConnectionId} 自动分配端口: {Port}",
                        config.Id, candidatePort);
                    return candidatePort;
                }
            }

            _logger.LogError("❌ 无法为 ConnectionId={ConnectionId} 分配可用端口", config.Id);
            return -1;
        }

        /// <summary>
        /// 检查端口是否可用
        /// </summary>
        private bool IsPortAvailable(int port)
        {
            try
            {
                using var listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                listener.Stop();
                return true;
            }
            catch
            {
                return false;
            }
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("正在停止所有 Modbus TCP Slave...");

            var stopTasks = _slaveInstances.Values.Select(instance => instance.StopAsync());
            await Task.WhenAll(stopTasks);

            _slaveInstances.Clear();
            AppTelemetry.Metrics.SetSlaveInstancesCount(0);
            _logger.LogInformation("✓ 所有 Slave 已停止");

            await base.StopAsync(cancellationToken);
        }

        /// <summary>
        /// 写入保持寄存器到指定连接的 Slave
        /// </summary>
        public Task WriteRegistersAsync(string connectionId, ushort startAddress, ushort[] values)
        {
            if (!_slaveInstances.TryGetValue(connectionId, out var instance))
            {
                _logger.LogWarning(
                    "未找到 Slave 实例: {ConnectionId} | 可用实例: [{Instances}]",
                    connectionId,
                    string.Join(", ", _slaveInstances.Keys));
                return Task.CompletedTask;
            }

            return instance.WriteRegistersAsync(startAddress, values);
        }

        /// <summary>
        /// 写入输入寄存器到指定连接的 Slave
        /// </summary>
        public Task WriteInputRegistersAsync(string connectionId, ushort startAddress, ushort[] values)
        {
            if (!_slaveInstances.TryGetValue(connectionId, out var instance))
            {
                _logger.LogWarning(
                    "未找到 Slave 实例: {ConnectionId} | 可用实例: [{Instances}]",
                    connectionId,
                    string.Join(", ", _slaveInstances.Keys));
                return Task.CompletedTask;
            }

            return instance.WriteInputRegistersAsync(startAddress, values);
        }

        /// <summary>
        /// 写入线圈到指定连接的 Slave
        /// </summary>
        public Task WriteCoilsAsync(string connectionId, ushort startAddress, bool[] values)
        {
            if (!_slaveInstances.TryGetValue(connectionId, out var instance))
            {
                _logger.LogWarning("未找到 Slave 实例: {ConnectionId}", connectionId);
                return Task.CompletedTask;
            }

            return instance.WriteCoilsAsync(startAddress, values);
        }

        /// <summary>
        /// 写入离散输入到指定连接的 Slave (新增)
        /// </summary>
        public Task WriteDiscreteInputsAsync(string connectionId, ushort startAddress, bool[] values)
        {
            if (!_slaveInstances.TryGetValue(connectionId, out var instance))
            {
                _logger.LogWarning(
                    "未找到 Slave 实例: {ConnectionId} | 可用实例: [{Instances}]",
                    connectionId,
                    string.Join(", ", _slaveInstances.Keys));
                return Task.CompletedTask;
            }

            return instance.WriteDiscreteInputsAsync(startAddress, values);
        }

        /// <summary>
        /// 获取指定 Slave 信息
        /// </summary>
        public Dictionary<string, object>? GetSlaveInfo(string connectionId)
        {
            if (!_slaveInstances.TryGetValue(connectionId, out var instance))
            {
                return null;
            }

            return new Dictionary<string, object>
            {
                ["ConnectionId"] = instance.ConnectionId,
                ["Port"] = instance.Port,
                ["SlaveId"] = instance.SlaveId,
                ["IsRunning"] = instance.IsRunning,
                ["Address"] = $"127.0.0.1:{instance.Port}"
            };
        }

        /// <summary>
        /// 获取所有 Slave 信息
        /// </summary>
        public List<Dictionary<string, object>> GetAllSlaveInfo()
        {
            return _slaveInstances.Values
                .Select(instance => new Dictionary<string, object>
                {
                    ["ConnectionId"] = instance.ConnectionId,
                    ["Port"] = instance.Port,
                    ["SlaveId"] = instance.SlaveId,
                    ["IsRunning"] = instance.IsRunning,
                    ["Address"] = $"127.0.0.1:{instance.Port}"
                })
                .OrderBy(info => (int)info["Port"])
                .ToList();
        }

        private async Task<List<ModbusConnectionConfig>> LoadConnectionConfigsAsync()
        {
            try
            {
                var configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "readModbus.json");

                _logger.LogInformation("🔍 查找配置文件: {Path}", configPath);

                if (!File.Exists(configPath))
                {
                    _logger.LogError("❌ 配置文件不存在: {Path}", configPath);
                    return new List<ModbusConnectionConfig>();
                }

                _logger.LogInformation("✓ 配置文件存在，开始读取...");

                var json = await File.ReadAllTextAsync(configPath);

                _logger.LogDebug("配置文件长度: {Length} 字符", json.Length);

                var config = JsonSerializer.Deserialize<ModbusConfig>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                });

                if (config == null)
                {
                    _logger.LogError("❌ 配置反序列化结果为null");
                    return new List<ModbusConnectionConfig>();
                }

                if (config.Connections == null)
                {
                    _logger.LogError("❌ 配置中没有connections节点");
                    return new List<ModbusConnectionConfig>();
                }

                _logger.LogInformation("✓ 配置解析成功，连接数: {Count}", config.Connections.Count);

                return config.Connections;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ 加载连接配置失败");
                _logger.LogError("异常详情: {Message}", ex.ToString());
                return new List<ModbusConnectionConfig>();
            }
        }

        /// <summary>
        /// 单个 Modbus TCP Slave 实例
        /// </summary>
        public class ModbusTcpSlaveInstance
        {
            private readonly ILogger _logger;
            private TcpListener? _listener;
            private IModbusTcpSlaveNetwork? _slaveNetwork;
            private readonly SlaveStorage _storage;
            private CancellationTokenSource? _listenerCts;

            public string ConnectionId { get; }
            public int Port { get; }
            public byte SlaveId { get; }
            public bool IsRunning => _listener != null && _listenerCts != null && !_listenerCts.IsCancellationRequested;

            public ModbusTcpSlaveInstance(
                string connectionId,
                int port,
                byte slaveId,
                ILogger logger)
            {
                ConnectionId = connectionId;
                Port = port;
                SlaveId = slaveId;
                _logger = logger;
                _storage = new SlaveStorage();
            }

            public async Task StartAsync(CancellationToken cancellationToken)
            {
                try
                {
                    _logger.LogDebug("开始启动Slave实例: {ConnectionId}, Port={Port}", ConnectionId, Port);

                    _listener = new TcpListener(IPAddress.Any, Port);

                    _logger.LogDebug("TcpListener已创建，开始Start...");

                    _listener.Start();

                    _logger.LogDebug("TcpListener.Start()完成");

                    var factory = new ModbusFactory();

                    _logger.LogDebug("ModbusFactory已创建");

                    var slave = factory.CreateSlave(SlaveId, _storage);

                    _logger.LogDebug("Slave已创建: SlaveId={SlaveId}", SlaveId);

                    _slaveNetwork = factory.CreateSlaveNetwork(_listener);

                    _logger.LogDebug("SlaveNetwork已创建");

                    _slaveNetwork.AddSlave(slave);

                    _logger.LogDebug("Slave已添加到Network");

                    _listenerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            _logger.LogDebug("开始监听: {ConnectionId}", ConnectionId);
                            await _slaveNetwork.ListenAsync(_listenerCts.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            _logger.LogDebug("Slave 监听已取消: {ConnectionId}", ConnectionId);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Slave 监听出错: {ConnectionId}", ConnectionId);
                        }
                    }, _listenerCts.Token);

                    // 等待一小段时间确保监听已启动
                    await Task.Delay(100, cancellationToken);

                    _logger.LogInformation(
                        "✅ Slave实例启动成功: ConnectionId={ConnectionId}, Port={Port}, SlaveId={SlaveId}",
                        ConnectionId, Port, SlaveId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "❌ 启动 Slave 失败: {ConnectionId}, Port={Port}", ConnectionId, Port);
                    _logger.LogError("异常详情: {Details}", ex.ToString());
                    throw;
                }
            }

            public async Task StopAsync()
            {
                try
                {
                    _listenerCts?.Cancel();
                    _slaveNetwork?.Dispose();
                    _listener?.Stop();

                    _logger.LogInformation("Slave 已停止: {ConnectionId}", ConnectionId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "停止 Slave 失败: {ConnectionId}", ConnectionId);
                }

                await Task.CompletedTask;
            }

            /// <summary>
            /// 写入保持寄存器
            /// </summary>
            public Task WriteRegistersAsync(ushort startAddress, ushort[] values)
            {
                try
                {
                    for (int i = 0; i < values.Length; i++)
                    {
                        _storage.SetHoldingRegister((ushort)(startAddress + i), values[i]);
                    }

                    _logger.LogTrace(
                        "写入保持寄存器: ConnectionId={ConnectionId}, Start={Start}, Count={Count}",
                        ConnectionId, startAddress, values.Length);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "写入保持寄存器失败: {ConnectionId}", ConnectionId);
                }

                return Task.CompletedTask;
            }

            /// <summary>
            /// 写入输入寄存器
            /// </summary>
            public Task WriteInputRegistersAsync(ushort startAddress, ushort[] values)
            {
                try
                {
                    for (int i = 0; i < values.Length; i++)
                    {
                        _storage.SetInputRegister((ushort)(startAddress + i), values[i]);
                    }

                    _logger.LogDebug(
                        "✅ 写入输入寄存器: ConnectionId={ConnectionId}, Start={Start}, Count={Count}, Sample=[{Sample}]",
                        ConnectionId, startAddress, values.Length,
                        string.Join(", ", values.Take(3).Select(v => $"0x{v:X4}")));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "❌ 写入输入寄存器失败: {ConnectionId}", ConnectionId);
                }

                return Task.CompletedTask;
            }

            /// <summary>
            /// 写入线圈 (Coil)
            /// </summary>
            public Task WriteCoilsAsync(ushort startAddress, bool[] values)
            {
                try
                {
                    for (int i = 0; i < values.Length; i++)
                    {
                        _storage.SetCoil((ushort)(startAddress + i), values[i]);
                    }

                    _logger.LogDebug(
                        "✅ 写入线圈: ConnectionId={ConnectionId}, Start={Start}, Count={Count}",
                        ConnectionId, startAddress, values.Length);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "❌ 写入线圈失败: {ConnectionId}", ConnectionId);
                }

                return Task.CompletedTask;
            }

            /// <summary>
            /// 写入离散输入 (Discrete Input) - 新增方法
            /// </summary>
            public Task WriteDiscreteInputsAsync(ushort startAddress, bool[] values)
            {
                try
                {
                    for (int i = 0; i < values.Length; i++)
                    {
                        _storage.SetDiscreteInput((ushort)(startAddress + i), values[i]);
                    }

                    _logger.LogDebug(
                        "✅ 写入离散输入: ConnectionId={ConnectionId}, Start={Start}, Count={Count}",
                        ConnectionId, startAddress, values.Length);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "❌ 写入离散输入失败: {ConnectionId}", ConnectionId);
                }

                return Task.CompletedTask;
            }
        }

        /// <summary>
        /// Slave 数据存储
        /// </summary>
        public class SlaveStorage : ISlaveDataStore
        {
            private readonly Dictionary<ushort, ushort> _holdingRegisters = new();
            private readonly Dictionary<ushort, ushort> _inputRegisters = new();
            private readonly Dictionary<ushort, bool> _coilDiscretes = new();
            private readonly Dictionary<ushort, bool> _coilInputs = new();

            public IPointSource<ushort> HoldingRegisters =>
                new DictionaryPointSource<ushort>(_holdingRegisters);

            public IPointSource<ushort> InputRegisters =>
                new DictionaryPointSource<ushort>(_inputRegisters);

            public IPointSource<bool> CoilDiscretes =>
                new DictionaryPointSource<bool>(_coilDiscretes);

            public IPointSource<bool> CoilInputs =>
                new DictionaryPointSource<bool>(_coilInputs);

            /// <summary>
            /// 设置保持寄存器
            /// </summary>
            public void SetHoldingRegister(ushort address, ushort value)
            {
                _holdingRegisters[address] = value;
            }

            /// <summary>
            /// 设置输入寄存器
            /// </summary>
            public void SetInputRegister(ushort address, ushort value)
            {
                _inputRegisters[address] = value;
            }

            /// <summary>
            /// 设置线圈 (Coil Discrete)
            /// </summary>
            public void SetCoil(ushort address, bool value)
            {
                _coilDiscretes[address] = value;
            }

            /// <summary>
            /// 设置离散输入 (Coil Input / Discrete Input)
            /// </summary>
            public void SetDiscreteInput(ushort address, bool value)
            {
                _coilInputs[address] = value;
            }
        }

        /// <summary>
        /// 字典点源实现
        /// </summary>
        public class DictionaryPointSource<T> : IPointSource<T>
        {
            private readonly Dictionary<ushort, T> _data;

            public DictionaryPointSource(Dictionary<ushort, T> data)
            {
                _data = data;
            }

            public T[] ReadPoints(ushort startAddress, ushort numberOfPoints)
            {
                var result = new T[numberOfPoints];
                for (ushort i = 0; i < numberOfPoints; i++)
                {
                    var address = (ushort)(startAddress + i);
                    result[i] = _data.TryGetValue(address, out var value) ? value : default!;
                }

                return result;
            }

            public void WritePoints(ushort startAddress, T[] points)
            {
                for (ushort i = 0; i < points.Length; i++)
                {
                    _data[(ushort)(startAddress + i)] = points[i];
                }
            }
        }
    }
}