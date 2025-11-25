PumpSupervisor 项目分析文档
项目概述
PumpSupervisor 是一个基于 .NET 8.0 的工业泵站监控系统，用于实时采集、处理和存储工业泵（乳化泵、喷雾泵）的运行数据，并提供远程监控和控制功能。

一、架构层面
1.1 整体架构设计
架构模式: DDD (领域驱动设计) + 事件驱动架构 (EDA) + 分层架构
核心层次:
┌─────────────────────────────────────────────────┐
│  API Layer (RESTful API + Swagger)              │
│  - ModbusConfigController                       │
└─────────────────────────────────────────────────┘
                      ↓
┌─────────────────────────────────────────────────┐
│  Application Layer (业务编排)                    │
│  - ModbusPollingService (轮询服务)              │
│  - ParameterChangeBusinessService (业务逻辑)     │
└─────────────────────────────────────────────────┘
                      ↓
┌─────────────────────────────────────────────────┐
│  Infrastructure Layer (基础设施)                 │
│  - Modbus通信 (NModbus)                         │
│  - 数据存储 (InfluxDB)                          │
│  - 消息发布 (MQTT)                              │
│  - 事件总线 (Wolverine)                         │
└─────────────────────────────────────────────────┘
                      ↓
┌─────────────────────────────────────────────────┐
│  Domain Layer (核心领域)                         │
│  - ModbusConfig (配置模型)                       │
│  - ModbusDataPoint (数据点)                      │
│  - Events (领域事件)                             │
└─────────────────────────────────────────────────┘
数据流向:
Modbus设备 → 轮询服务读取 → 数据采集事件 → 数据解析 → 
InfluxDB存储 + MQTT发布 → 业务逻辑处理 → 控制指令写回
关键设计模式:

事件驱动: 使用 Wolverine 消息总线处理 ModbusDataCollectedEvent, DataParsedEvent, ParameterValueChangedEvent 等事件
虚拟 Slave 模式: 通过 ModbusTcpSlaveService 创建虚拟 Modbus 从站，将采集的数据同步到本地虚拟设备供第三方系统读取
配置外部化: 设备配置支持外部 JSON 文件，通过 path 字段引用

1.2 技术栈
类别技术版本用途运行时.NET8.0应用程序框架Web框架ASP.NET Core内置RESTful APIModbus通信NModbus3.0.81Modbus TCP/RTU 协议时序数据库InfluxDB Client4.18.0传感器数据存储消息队列MQTTnet5.0.1实时数据发布事件总线WolverineFx5.0.0内部消息传递日志Serilog4.3.0结构化日志可观测性OpenTelemetry1.13+链路追踪与指标API文档Swashbuckle9.0.6Swagger UI缓存MemoryCache内置配置缓存
1.3 依赖关系
核心依赖图:
ModbusPollingService
    ├── IModbusConnectionManager (连接管理)
    ├── IMessageBus (事件发布)
    ├── IModbusConfigCacheService (配置读取)
    └── ModbusTcpSlaveService (虚拟Slave同步)

ModbusReadCommandHandler
    ├── IModbusConnectionManager
    └── IModbusConfigCacheService

DataCollectedEventHandler
    ├── IModbusDataParser (数据解析)
    ├── IModbusConfigCacheService
    └── IParameterValueTracker (值变化检测)

DataParsedEventHandler
    └── IInfluxDbService (存储)

DataStoredEventHandler
    └── IMqttPublisher (发布)

ParameterChangeBusinessHandler
    └── ParameterChangeBusinessService (业务逻辑)
关键接口:

IModbusConnectionManager: Modbus 连接池管理
IModbusConfigCacheService: 配置缓存服务
IModbusDataParser: 数据类型解析（float32, uint16, bit_map 等）
IInfluxDbService: InfluxDB 写入
IMqttPublisher: MQTT 消息发布


二、业务层面
2.1 核心业务逻辑
主要功能:

数据采集:

支持 Modbus TCP/RTU 协议
三种轮询模式: periodic(周期), continuous(连续), on_demand(按需)
多设备并发采集，优先级队列处理读写请求


数据处理:

多种数据类型解析: float32, uint32, uint16, bit, bit_map, enum_map
支持 4 种字节序: ABCD, DCBA, BADC, CDAB
参数值变化检测 (on_change 机制)


数据存储与发布:

InfluxDB 时序数据库存储
MQTT 实时消息发布
虚拟 Modbus Slave 同步（供第三方系统读取）


业务逻辑:

压力/液位报警处理 (自动开启泄压阀、启动备用泵)
泵联控启停逻辑
运行模式切换 (手动/自动/检修)



2.2 数据模型
核心实体:
csharp// 配置模型
ModbusConfig
    ├── List<ModbusConnectionConfig> Connections
    └── List<AutoCreateDeviceConfig> AutoCreateDevices

ModbusConnectionConfig
    ├── string Id, Name
    ├── string Type (TCP/RTU)
    ├── int SlaveId, SlavePort
    ├── ConnectionSettings Connection
    └── List<DeviceConfig> Devices

DeviceConfig
    ├── string Id, Name, PollMode
    ├── List<ReadBlock> ReadBlocks
    └── List<ParameterConfig> Parameters

ParameterConfig
    ├── string Code, Name, DataType
    ├── List<int> Address
    ├── double Scale, Offset, int Precision
    ├── Dictionary<string, BitMapItem> BitMap
    └── Dictionary<string, string> EnumMap

// 运行时数据模型
ModbusDataPoint
    ├── string ConnectionId, DeviceId, ParameterCode
    ├── object RawValue, ParsedValue
    ├── DateTime Timestamp
    └── Dictionary<string, object> Metadata

ModbusDataBatch
    ├── string ConnectionId, DeviceId
    ├── List<ModbusDataPoint> DataPoints
    └── DateTime Timestamp
```

**InfluxDB 数据结构**:
```
Measurement: nbcb_collect_pump_sensor_data
Tags: connection_id, device_id, parameter_code
Field: value (double 类型)
Timestamp: ms 精度
```

### 2.3 用户角色与权限

**当前版本**: 无明确的用户权限系统，API 未启用认证

**访问控制**:
- 配置 API: `http://localhost:5100/api/modbusconfig` (只读)
- Swagger UI: `http://localhost:5100/swagger` (开发/调试)
- 虚拟 Slave 端口: 按配置自动分配 (60000+ 或配置的 `slave_port`)

---

## 三、代码层面

### 3.1 代码组织结构
```
PumpSupervisor/
├── API/                              # RESTful API 层
│   ├── Controllers/
│   │   └── ModbusConfigController.cs
│   └── ModbusConfigApiService.cs     # API 托管服务
├── Application/                      # 应用服务层
│   ├── Controller/
│   │   └── PumpController.cs        # (已注释，未使用)
│   └── Services/
│       ├── ModbusPollingService.cs  # 轮询协调
│       ├── ModbusApiService.cs      # 按需读写 API
│       ├── ParameterChangeBusinessService.cs  # 业务逻辑
│       ├── DataBatchCacheService.cs
│       ├── ParameterValueTracker.cs
│       └── StartupCoordinator.cs
├── Domain/                           # 领域模型层
│   ├── Configuration/
│   │   └── ApiSettings.cs
│   ├── Events/
│   │   └── ModbusDataCollectedEvent.cs
│   └── Models/
│       ├── ModbusConfig.cs
│       ├── ModbusConnectionConfig.cs
│       ├── ModbusDataPoint.cs
│       └── AutoCreateDeviceConfig.cs
├── Infrastructure/                   # 基础设施层
│   ├── Cache/
│   │   └── ModbusConfigCacheService.cs
│   ├── Configuration/
│   │   └── ModbusConfigLoader.cs    # 支持外部配置文件加载
│   ├── Messaging/
│   │   └── Mqtt/
│   │       └── MqttPublisher.cs
│   ├── Modbus/
│   │   ├── Commands/
│   │   │   └── ModbusCommands.cs
│   │   ├── DataParser/
│   │   │   └── ModbusDataParser.cs  # 数据类型解析
│   │   ├── Factory/
│   │   │   ├── IModbusConnection.cs
│   │   │   ├── ModbusTcpConnection.cs
│   │   │   └── ModbusRtuConnection.cs
│   │   ├── Handlers/
│   │   │   ├── ModbusReadCommandHandler.cs
│   │   │   ├── DataCollectedEventHandler.cs
│   │   │   ├── DataParsedEventHandler.cs
│   │   │   ├── DataStoredEventHandler.cs
│   │   │   └── ParameterChangeBusinessHandler.cs
│   │   ├── Queries/
│   │   │   └── ModbusQueries.cs
│   │   ├── IModbusConnectionManager.cs
│   │   └── PriorityModbusCommandQueue.cs
│   ├── Storage/
│   │   ├── InfluxDb/
│   │   │   └── InfluxDbService.cs
│   │   └── ModbusSlave/
│   │       └── ModbusTcpSlaveService.cs
│   └── Telemetry/
│       ├── AppTelemetry.cs
│       └── MetricsCollectionService.cs
├── DevicesConfigs/                   # 设备配置文件
│   ├── emulsion_master/
│   ├── emulsion01-05/
│   ├── atomizing_master/
│   ├── atomizing01-03/
│   └── emulsion_switch_rtu/
├── Program.cs                        # 启动入口
├── appsettings.json                  # 应用配置
└── readModbus.json                   # Modbus 主配置
```

**命名规范**:
- **接口**: `I` 前缀 (如 `IModbusConnectionManager`)
- **事件**: `Event` 后缀 (如 `ModbusDataCollectedEvent`)
- **处理器**: `Handler` 后缀 (如 `DataCollectedEventHandler`)
- **服务**: `Service` 后缀 (如 `ModbusPollingService`)
- **配置**: `Config` 后缀 (如 `DeviceConfig`)

### 3.2 关键入口点

**启动流程** (`Program.cs`):
```
1. 加载配置 (appsettings.json)
2. 初始化 Serilog 日志
3. 注册服务依赖
4. 初始化配置缓存 (InitializeConfigCacheAsync)
5. 启动托管服务:
   - StartupCoordinator
   - ModbusTcpSlaveService (创建虚拟Slave)
   - ModbusConfigApiService (RESTful API)
   - ModbusPollingService (轮询服务)
   - MqttPublisher
   - InfluxDbService
   - MetricsCollectionService
6. 启动 Wolverine 事件总线
```

**API 端点** (`ModbusConfigController`):
```
GET  /api/modbusconfig                               # 完整配置
GET  /api/modbusconfig/connections                   # 连接列表
GET  /api/modbusconfig/connections/{id}              # 指定连接
GET  /api/modbusconfig/connections/{id}/devices      # 设备列表
GET  /api/modbusconfig/connections/{cid}/devices/{did}          # 指定设备
GET  /api/modbusconfig/connections/{cid}/devices/{did}/parameters  # 参数列表
POST /api/modbusconfig/refresh                       # 刷新缓存
GET  /api/modbusconfig/health                        # 健康检查
```

**事件处理链**:
```
ReadModbusDataCommand
    → ModbusReadCommandHandler
    → ModbusDataCollectedEvent
    → DataCollectedEventHandler
    → DataParsedEvent
    → DataParsedEventHandler
    → DataStoredEvent
    → DataStoredEventHandler (MQTT发布)

ParameterValueChangedEvent
    → ParameterChangeBusinessHandler (业务逻辑处理)
```

### 3.3 现有测试

**当前状态**: ❌ **项目中未包含单元测试或集成测试**

**测试覆盖建议**:
1. `ModbusDataParser` 的数据类型解析 (float32, bit_map, 字节序)
2. `ModbusConnectionManager` 的连接复用逻辑
3. `ParameterChangeBusinessService` 的业务规则
4. `ModbusTcpSlaveService` 的虚拟Slave创建

---

## 四、各组件功能说明

### 4.1 核心服务组件

| 组件 | 功能 | 关键方法 |
|------|------|----------|
| **ModbusPollingService** | 轮询协调器，管理所有设备的数据采集 | `StartPeriodicPolling`, `StartContinuousPolling` |
| **ModbusConnectionManager** | Modbus 连接池，复用 TCP/RTU 连接 | `GetConnectionAsync`, `EnsureConnectedAsync` |
| **ModbusConfigCacheService** | 配置缓存，支持刷新 | `GetConfigAsync`, `RefreshConfigAsync` |
| **ModbusTcpSlaveService** | 虚拟 Modbus Slave，供第三方系统读取 | `WriteRegistersAsync`, `WriteInputRegistersAsync` |
| **InfluxDbService** | InfluxDB 写入服务 | `WriteDataBatchAsync` |
| **MqttPublisher** | MQTT 消息发布 | `PublishDataBatchAsync`, `PublishValueChangeAsync` |
| **ParameterChangeBusinessService** | 业务逻辑处理（报警、联控） | `ReadParameterValueAsync`, `WriteRegistersAsync` |

### 4.2 数据处理组件

| 组件 | 功能 | 支持类型 |
|------|------|----------|
| **ModbusDataParser** | 数据类型解析 | `float32`, `uint32`, `int32`, `uint16`, `int16`, `bit`, `string` |
| **DataCollectedEventHandler** | 原始数据解析为业务对象 | 支持 `bit_map`, `enum_map`, `scale`, `offset` |
| **ParameterValueTracker** | 参数值变化检测 | 缓存上次值，触发 `on_change` 事件 |

### 4.3 配置加载

| 组件 | 功能 |
|------|------|
| **ModbusConfigLoader** | 加载主配置 + 外部设备配置文件 |
| **外部配置支持** | 通过 `path` 字段引用 `/DevicesConfigs/xxx/xxx.json` |

**配置优先级**:
```
readModbus.json (主配置)
    └── device.path → /DevicesConfigs/emulsion01/emulsion01.json
                      (覆盖 poll_mode, read_blocks, parameters)

五、运维与约束
5.1 部署方式
构建:
bashdotnet publish -c Release -r win-x64 --self-contained
运行方式:

控制台模式: PumpSupervisor.exe
Windows 服务: 使用 UseWindowsService() 托管

配置文件:

appsettings.json: 基础设施配置（InfluxDB, MQTT, OpenTelemetry）
readModbus.json: Modbus 设备配置（连接、设备、参数）
/DevicesConfigs/**/*.json: 外部设备配置

环境变量: 无特殊要求，所有配置通过 JSON 文件管理
5.2 性能要求
指标要求备注采集频率periodic: 1-2s, continuous: 10-100ms可通过 poll_interval 和 min_poll_interval 配置并发连接数10+ Modbus 连接连接池复用，每个连接独立管理数据点吞吐1000+ 点/秒批量写入 InfluxDB虚拟 Slave 数量10+ 实例每个连接可创建独立虚拟Slave内存占用< 500MB稳定运行状态
5.3 已知问题与技术债

无单元测试: 缺少自动化测试，依赖人工验证
配置验证不完整: 启动时未全面检查配置合法性（如地址重叠、read_blocks 覆盖范围）
错误重试机制: Modbus 读写失败后的重试策略较简单
日志过滤: 部分 Wolverine 内部日志未完全过滤
API 认证: RESTful API 未启用身份验证
硬编码业务逻辑: ParameterChangeBusinessHandler 中的业务规则硬编码在 switch 语句中


六、文档与历史
6.1 配置文档
readModbus.json 核心配置项:
配置项说明示例connections[].type连接类型TCP, RTUconnections[].register_type寄存器类型holding, input, coil, discrete_inputconnections[].slave_idModbus 从站 ID1-247connections[].slave_port虚拟 Slave 端口 (可选)5020, 未配置则自动分配devices[].poll_mode轮询模式periodic, continuous, on_demanddevices[].path外部配置文件路径/DevicesConfigs/emulsion01/emulsion01.jsondevices[].read_blocks读取块定义[{start: 0, count: 10}]parameters[].data_type数据类型float32, uint16, bit, etc.parameters[].bit_map位映射 (uint16拆分为多个布尔值){"0": {code: "xxx", name: "xxx"}}parameters[].enum_map枚举映射{"0": "手动", "1": "自动"}parameters[].on_change值变化触发事件true, false
字节序说明:

ABCD: Big Endian (Motorola, 常用)
DCBA: Little Endian (Intel)
BADC: Big Endian with byte swap
CDAB: Little Endian with byte swap

6.2 API 文档
Swagger UI: http://localhost:5100/swagger
核心接口:

配置查询: 只读，用于前端展示和第三方集成
健康检查: 用于监控系统可用性

6.3 变更历史
关键特性演进:

✅ 配置外部化: 支持通过 path 引用外部设备配置
✅ 虚拟 Slave: 自动创建 Modbus TCP Slave 供第三方系统读取
✅ 三种轮询模式: periodic, continuous, on_demand
✅ 参数变化检测: on_change 机制触发业务逻辑
✅ OpenTelemetry 集成: 链路追踪、指标收集
✅ MQTT 实时发布: 数据变化实时推送


七、快速上手指南
7.1 本地开发环境搭建
前置条件:

.NET 8.0 SDK
InfluxDB 2.x (可选，禁用则跳过存储)
MQTT Broker (可选，如 Mosquitto)
Modbus 模拟器 (如 ModbusSlave/ModbusPoll)

启动步骤:
bash# 1. 克隆项目
git clone <repository-url>

# 2. 修改配置
# - appsettings.json: 更新 InfluxDB/MQTT 地址
# - readModbus.json: 配置 Modbus 连接

# 3. 运行
dotnet run --project PumpSupervisor

# 4. 访问 Swagger
http://localhost:5100/swagger
7.2 添加新设备配置
步骤:

在 /DevicesConfigs/your_device/ 创建 JSON 文件
在 readModbus.json 中添加设备引用:

json{
  "id": "your_device",
  "name": "Your Device",
  "enabled": true,
  "path": "/DevicesConfigs/your_device/your_device.json"
}

调用 POST /api/modbusconfig/refresh 刷新配置

7.3 添加新业务逻辑
位置: ParameterChangeBusinessHandler.ProcessParameterChangeAsync
示例:
csharpcase "your_parameter_code":
    await HandleYourBusinessLogicAsync(@event, cancellationToken);
    break;

八、常见问题
Q1: 虚拟 Slave 端口如何分配？

优先使用配置的 slave_port
未配置则从 60000 开始自动分配

Q2: 如何查看虚拟 Slave 列表？

查看启动日志中的 "虚拟共享Slave 实例列表"
或通过代码调用 ModbusTcpSlaveService.GetAllSlaveInfo()

Q3: 参数值变化事件何时触发？

on_change: true 且参数值与上次不同时触发
浮点数比较使用 precision 配置的精度

Q4: 为什么 InfluxDB 没有数据？

检查 appsettings.json 中的 InfluxDB Token 是否正确
检查 Bucket 和 Org 是否存在

Q5: 如何调试 Modbus 通信？

将日志级别设为 Debug: appsettings.json → Serilog.MinimumLevel.Default
查看 Seq 日志: http://localhost:5341


九、后续优化建议

添加单元测试: 覆盖核心数据解析和业务逻辑
配置验证增强: 启动时全面检查配置合法性
业务规则引擎: 将硬编码的业务逻辑改为规则引擎驱动
API 认证: 添加 JWT 或 API Key 认证
Web 管理界面: 可视化配置管理和实时监控
Grafana 仪表盘: 基于 InfluxDB 数据的可视化面板


文档版本: 1.0
最后更新: 2025-01-XX
维护者: [Your Name/Team]