using Microsoft.Extensions.Logging;
using PumpSupervisor.Application.Services;
using PumpSupervisor.Domain.Events;

namespace PumpSupervisor.Infrastructure.Modbus.Handlers
{
    public class ParameterChangeBusinessHandler
    {
        private readonly ILogger<ParameterChangeBusinessHandler> _logger;
        private readonly ParameterChangeBusinessService _businessService;

        public ParameterChangeBusinessHandler(
            ILogger<ParameterChangeBusinessHandler> logger,
            ParameterChangeBusinessService businessService)
        {
            _logger = logger;
            _businessService = businessService;
        }

        public async Task Handle(ParameterValueChangedEvent @event, CancellationToken cancellationToken)
        {
            var key = $"{@event.ConnectionId}:{@event.DeviceId}:{@event.ParameterCode}";

            if (!_businessService.ShouldProcess(key))
            {
                return;
            }

            _logger.LogInformation(
                "🔔 处理参数变化业务逻辑: {ConnectionId}/{DeviceId}/{ParamCode}, 0x{OldValue:X4} → 0x{NewValue:X4}",
                @event.ConnectionId,
                @event.DeviceId,
                @event.ParameterCode,
                @event.OldValue,
                @event.NewValue);

            try
            {
                await ProcessParameterChangeAsync(@event, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ 处理参数变化业务逻辑失败: {ParamCode}", @event.ParameterCode);
            }
        }

        private async Task ProcessParameterChangeAsync(
            ParameterValueChangedEvent @event,
            CancellationToken cancellationToken)
        {
            // ✅ 根据参数代码路由到不同的处理方法
            switch (@event.ParameterCode)
            {
                //乳化泵急停
                case "emulsionPumpEmergencyStop":
                    await HandleEmulsionPumpEmergencyStopAsync(@event, cancellationToken);
                    break;

                //乳化泵联控启停
                case "emulsionPumpGroupStartStop":
                    await HandleEmulsionPumpGroupStartStopAsync(@event, cancellationToken);
                    break;

                //乳化泵泄压阀
                case "emulsion01ValveSwitch":
                case "emulsion02ValveSwitch":
                case "emulsion03ValveSwitch":
                case "emulsion04ValveSwitch":
                case "emulsion05ValveSwitch":
                    await HandleEmulsionPumpValveSwitchAsync(@event, cancellationToken);
                    break;

                // 带 bit_map 的报警状态参数
                case "emulsionMasterPressureWarningState":
                    await HandleEmulsionPressureWarningAsync(@event, cancellationToken);
                    break;

                case "emulsionMasterWaterBoxLevelWarningState":
                    await HandleEmulsionWaterLevelWarningAsync(@event, cancellationToken);
                    break;

                case "atomizingMasterPressureWarningState":
                    await HandleAtomizingPressureWarningAsync(@event, cancellationToken);
                    break;

                case "atomizingMasterWaterBoxLevelWarningState":
                    await HandleAtomizingWaterLevelWarningAsync(@event, cancellationToken);
                    break;

                // 泵状态参数 (带 bit_map)
                case "emulsion01OilPressureWarningState":
                case "emulsion01CrankcaseOilLevelWarningState":
                case "emulsion01CrankcaseTemperatureWarningState":
                case "emulsion01MotorTemperatureWarningState":
                    await HandlePumpWarningStateAsync(@event, cancellationToken);
                    break;

                // 普通数值参数
                case "temperature":
                    await HandleTemperatureChangeAsync(@event, cancellationToken);
                    break;

                case "pump_status":
                    await HandlePumpStatusChangeAsync(@event, cancellationToken);
                    break;

                case "pressure":
                    await HandlePressureChangeAsync(@event, cancellationToken);
                    break;

                case "flow_rate":
                    await HandleFlowRateChangeAsync(@event, cancellationToken);
                    break;

                // 通用报警处理
                default:
                    if (@event.ParameterCode.EndsWith("_alarm") ||
                        @event.ParameterCode.Contains("Alarm") ||
                        @event.ParameterCode.Contains("Warning"))
                    {
                        await HandleGenericAlarmAsync(@event, cancellationToken);
                    }
                    break;
            }
        }

        #region 按钮动作处理

        /// <summary>
        /// 乳化泵急停处理
        /// </summary>
        /// <param name="event"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async Task HandleEmulsionPumpEmergencyStopAsync(ParameterValueChangedEvent @event, CancellationToken cancellationToken)
        {
            _logger.LogInformation("⚙️ 处理乳化泵急停: {ConnectionId}/{DeviceId}/{ParameterCode}", @event.ConnectionId, @event.DeviceId, @event.ParameterCode);

            if (@event.NewValue is bool emergencySwitch)
            {
                // 1. 先读取当前模式
                var currentData = await _businessService.ReadParameterValueAsync("emulsion_system_tcp", "emulsion01", "emulsion01WorkingMode", cancellationToken);
                if (currentData is string workingMode)
                {
                    if ("检修".Equals(workingMode, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogWarning("🚨 已收到急停开关命令，当前处于检修模式不响应任何控制命令!");
                    }
                    else
                    {
                        if (emergencySwitch)
                        {
                            _logger.LogWarning("⚠️ 乳化系统急停开启!");
                            await _businessService.WriteSingleRegisterAsync("emulsion_system_tcp", "emulsion_control_status", address: 1503, value: 1, cancellationToken);
                        }
                        else
                        {
                            _logger.LogWarning("⚠️ 乳化系统急停关闭!");
                            await _businessService.WriteSingleRegisterAsync("emulsion_system_tcp", "emulsion_control_status", address: 1503, value: 0, cancellationToken);
                        }
                    }
                }
                else
                {
                    _logger.LogError("🚨 乳化泵运行模式读取异常，请检查配置!");
                }
            }
        }

        /// <summary>
        /// 乳化泵联控启停处理
        /// </summary>
        /// <param name="event"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async Task HandleEmulsionPumpGroupStartStopAsync(ParameterValueChangedEvent @event, CancellationToken cancellationToken)
        {
            _logger.LogInformation("⚙️ 处理乳化泵联控启停: {ConnectionId}/{DeviceId}/{ParameterCode}", @event.ConnectionId, @event.DeviceId, @event.ParameterCode);

            if (@event.NewValue is bool groupStartStop)
            {
                // 1. 先读取当前模式
                var currentData = await _businessService.ReadParameterValueAsync("emulsion_system_tcp", "emulsion01", "emulsion01WorkingMode", cancellationToken);
                if (currentData is string workingMode)
                {
                    if ("检修".Equals(workingMode, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogWarning("🚨 已收到急停开关命令，当前处于检修模式不响应任何控制命令!");
                    }
                    if ("手动".Equals(workingMode, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogWarning("🚨 已收到联控泵组开关命令，当前处于手动模式不响应此控制命令!");
                    }
                    else
                    {
                        // 2.读取当前联控状态
                        var groupState = await _businessService.ReadParameterValueAsync("emulsion_system_tcp", "emulsion_master", "emulsionMaster_jointControl_status", cancellationToken);
                        if (groupState is string emulsionPumpGroupState)
                        {
                            if ("启动".Equals(emulsionPumpGroupState, StringComparison.OrdinalIgnoreCase))
                            {
                                if (!groupStartStop)//对于开关从 0变1 先不处理，等到从1变0的时候再处理
                                {
                                    _logger.LogWarning("⚠️ 乳化泵联控停止!");
                                    await _businessService.WriteSingleRegisterAsync("emulsion_system_tcp", "emulsion_control_status", address: 1501, value: 1, cancellationToken);
                                    await Task.Delay(1000);//等待特定的秒数后再复位联控状态
                                    await _businessService.WriteSingleRegisterAsync("emulsion_system_tcp", "emulsion_control_status", address: 1501, value: 0, cancellationToken);
                                }
                            }
                            else
                            {
                                if (!groupStartStop)//对于开关从 0变1 先不处理，等到从1变0的时候再处理
                                {
                                    _logger.LogWarning("⚠️ 乳化泵联控启动!");
                                    await _businessService.WriteSingleRegisterAsync("emulsion_system_tcp", "emulsion_control_status", address: 1500, value: 1, cancellationToken);
                                    await Task.Delay(1000);//等待特定的秒数后再复位联控状态
                                    await _businessService.WriteSingleRegisterAsync("emulsion_system_tcp", "emulsion_control_status", address: 1500, value: 0, cancellationToken);
                                }
                            }
                        }
                        else
                        {
                            _logger.LogError("🚨 乳化泵联控状态读取异常，请检查配置!");
                        }
                    }
                }
                else
                {
                    _logger.LogError("🚨 乳化泵运行模式读取异常，请检查配置!");
                }
            }
        }

        /// <summary>
        /// 乳化泵泄压阀处理
        /// </summary>
        /// <param name="event"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async Task HandleEmulsionPumpValveSwitchAsync(ParameterValueChangedEvent @event, CancellationToken cancellationToken)
        {
            _logger.LogInformation("⚙️ 处理乳化泵泄压阀: {ConnectionId}/{DeviceId}/{ParameterCode}", @event.ConnectionId, @event.DeviceId, @event.ParameterCode);

            if (@event.NewValue is bool pumpValveSwitch)
            {
                // 1. 先读取当前模式
                var currentData = await _businessService.ReadParameterValueAsync("emulsion_system_tcp", "emulsion01", "emulsion01WorkingMode", cancellationToken);
                if (currentData is string workingMode)
                {
                    if ("检修".Equals(workingMode, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogWarning("🚨 已收到泄压阀开关命令，当前处于检修模式不响应任何控制命令!");
                    }
                    if ("自动".Equals(workingMode, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogWarning("🚨 已收到泄压阀开关命令，当前处于自动模式不响应泄压阀开关命令!");
                    }
                    else
                    {
                        switch (@event.ParameterCode)
                        {
                            case "emulsion01ValveSwitch":
                                if (pumpValveSwitch)
                                {
                                    _logger.LogWarning("⚠️ emulsion01泄压阀开启!");
                                    await _businessService.WriteSingleRegisterAsync("emulsion_system_tcp", "emulsion01_control_status", address: 82, value: 1, cancellationToken);
                                }
                                else
                                {
                                    _logger.LogWarning("⚠️ emulsion01泄压阀关闭!");
                                    await _businessService.WriteSingleRegisterAsync("emulsion_system_tcp", "emulsion01_control_status", address: 82, value: 0, cancellationToken);
                                }
                                break;

                            case "emulsion02ValveSwitch":
                                if (pumpValveSwitch)
                                {
                                    _logger.LogWarning("⚠️ emulsion02泄压阀开启!");
                                    await _businessService.WriteSingleRegisterAsync("emulsion_system_tcp", "emulsion02_control_status", address: 382, value: 1, cancellationToken);
                                }
                                else
                                {
                                    _logger.LogWarning("⚠️ emulsion02泄压阀关闭!");
                                    await _businessService.WriteSingleRegisterAsync("emulsion_system_tcp", "emulsion02_control_status", address: 382, value: 0, cancellationToken);
                                }
                                break;

                            case "emulsion03ValveSwitch":
                                if (pumpValveSwitch)
                                {
                                    _logger.LogWarning("⚠️ emulsion03泄压阀开启!");
                                    await _businessService.WriteSingleRegisterAsync("emulsion_system_tcp", "emulsion03_control_status", address: 682, value: 1, cancellationToken);
                                }
                                else
                                {
                                    _logger.LogWarning("⚠️ emulsion03泄压阀关闭!");
                                    await _businessService.WriteSingleRegisterAsync("emulsion_system_tcp", "emulsion03_control_status", address: 682, value: 0, cancellationToken);
                                }
                                break;

                            case "emulsion04ValveSwitch":

                                if (pumpValveSwitch)
                                {
                                    _logger.LogWarning("⚠️ emulsion04泄压阀开启!");
                                    await _businessService.WriteSingleRegisterAsync("emulsion_system_tcp", "emulsion04_control_status", address: 982, value: 1, cancellationToken);
                                }
                                else
                                {
                                    _logger.LogWarning("⚠️ emulsion04泄压阀关闭!");
                                    await _businessService.WriteSingleRegisterAsync("emulsion_system_tcp", "emulsion04_control_status", address: 982, value: 0, cancellationToken);
                                }
                                break;

                            case "emulsion05ValveSwitch":
                                if (pumpValveSwitch)
                                {
                                    _logger.LogWarning("⚠️ emulsion05泄压阀开启!");
                                    await _businessService.WriteSingleRegisterAsync("emulsion_system_tcp", "emulsion05_control_status", address: 1282, value: 1, cancellationToken);
                                }
                                else
                                {
                                    _logger.LogWarning("⚠️ emulsion05泄压阀关闭!");
                                    await _businessService.WriteSingleRegisterAsync("emulsion_system_tcp", "emulsion05_control_status", address: 1282, value: 0, cancellationToken);
                                }
                                break;
                        }
                    }
                }
                else
                {
                    _logger.LogError("🚨 乳化泵运行模式读取异常，请检查配置!");
                }
            }
        }

        #endregion 按钮动作处理

        /// <summary>
        /// 处理乳化系统压力报警状态变化
        /// </summary>
        private async Task HandleEmulsionPressureWarningAsync(
            ParameterValueChangedEvent @event,
            CancellationToken cancellationToken)
        {
            _logger.LogInformation("⚙️ 处理乳化系统压力报警: {ConnectionId}/{DeviceId}",
                @event.ConnectionId, @event.DeviceId);

            // ✅ 从 DataPoint.ParsedValue 获取 bit_map 详细状态
            if (@event.DataPoint?.ParsedValue is Dictionary<string, bool> bitMap)
            {
                bool highAlarm = bitMap.GetValueOrDefault("emulsionMasterPressureAlarm_HighAlarm");
                bool highWarning = bitMap.GetValueOrDefault("emulsionMasterPressureAlarm_HighWarning");
                bool lowWarning = bitMap.GetValueOrDefault("emulsionMasterPressureAlarm_LowWarning");
                bool lowAlarm = bitMap.GetValueOrDefault("emulsionMasterPressureAlarm_LowAlarm");

                _logger.LogDebug("压力报警状态: HighAlarm={HighAlarm}, HighWarning={HighWarning}, LowWarning={LowWarning}, LowAlarm={LowAlarm}",
                    highAlarm, highWarning, lowWarning, lowAlarm);

                // 处理高压报警
                if (highAlarm)
                {
                    _logger.LogError("🚨 乳化系统压力高报警触发!");

                    // 1. 立即打开泄压阀
                    await _businessService.WriteSingleRegisterAsync(
                        @event.ConnectionId,
                        "emulsion_master",
                        address: 300,  // 泄压阀控制地址
                        value: 1,
                        cancellationToken);

                    // 2. 降低泵转速
                    await _businessService.WriteSingleRegisterAsync(
                        @event.ConnectionId,
                        "emulsion_master",
                        address: 301,
                        value: 50,  // 降低到50%
                        cancellationToken);

                    _logger.LogInformation("✅ 已执行高压保护: 泄压阀已打开, 泵转速降至50%");
                }
                else if (highWarning)
                {
                    _logger.LogWarning("⚠️ 乳化系统压力高预警");

                    // 预警时降低10%转速
                    var currentData = await _businessService.ReadRegistersAsync(
                        @event.ConnectionId,
                        "emulsion_master",
                        cancellationToken);

                    if (currentData != null && currentData.TryGetValue(301, out var currentSpeed))
                    {
                        var newSpeed = (ushort)(currentSpeed * 0.9);
                        await _businessService.WriteSingleRegisterAsync(
                            @event.ConnectionId,
                            "emulsion_master",
                            address: 301,
                            value: newSpeed,
                            cancellationToken);

                        _logger.LogInformation("✅ 预警响应: 转速 {Old}% → {New}%", currentSpeed, newSpeed);
                    }
                }

                // 处理低压报警
                if (lowAlarm)
                {
                    _logger.LogError("🚨 乳化系统压力低报警触发!");

                    // 1. 关闭泄压阀
                    await _businessService.WriteSingleRegisterAsync(
                        @event.ConnectionId,
                        "emulsion_master",
                        address: 300,
                        value: 0,
                        cancellationToken);

                    // 2. 启动备用泵
                    await _businessService.WriteSingleRegisterAsync(
                        @event.ConnectionId,
                        "emulsion_master",
                        address: 400,  // 备用泵启动
                        value: 1,
                        cancellationToken);

                    _logger.LogInformation("✅ 已执行低压保护: 泄压阀已关闭, 备用泵已启动");
                }
                else if (lowWarning)
                {
                    _logger.LogWarning("⚠️ 乳化系统压力低预警");

                    // 预警时提高10%转速
                    var currentData = await _businessService.ReadRegistersAsync(
                        @event.ConnectionId,
                        "emulsion_master",
                        cancellationToken);

                    if (currentData != null && currentData.TryGetValue(301, out var currentSpeed))
                    {
                        var newSpeed = (ushort)Math.Min(100, currentSpeed * 1.1);
                        await _businessService.WriteSingleRegisterAsync(
                            @event.ConnectionId,
                            "emulsion_master",
                            address: 301,
                            value: newSpeed,
                            cancellationToken);

                        _logger.LogInformation("✅ 预警响应: 转速 {Old}% → {New}%", currentSpeed, newSpeed);
                    }
                }
            }
            else
            {
                _logger.LogWarning("⚠️ 无法获取压力报警详细状态 (ParsedValue 不是 Dictionary<string, bool>)");
            }
        }

        /// <summary>
        /// 处理乳化液箱液位报警状态变化
        /// </summary>
        private async Task HandleEmulsionWaterLevelWarningAsync(
            ParameterValueChangedEvent @event,
            CancellationToken cancellationToken)
        {
            _logger.LogInformation("💧 处理乳化液箱液位报警: {ConnectionId}/{DeviceId}",
                @event.ConnectionId, @event.DeviceId);

            if (@event.DataPoint?.ParsedValue is Dictionary<string, bool> bitMap)
            {
                bool highAlarm = bitMap.GetValueOrDefault("emulsionMasterWaterBoxLevel_HighAlarm");
                bool highWarning = bitMap.GetValueOrDefault("emulsionMasterWaterBoxLevel_HighWarning");
                bool lowWarning = bitMap.GetValueOrDefault("emulsionMasterWaterBoxLevel_LowWarning");
                bool lowAlarm = bitMap.GetValueOrDefault("emulsionMasterWaterBoxLevel_LowAlarm");

                _logger.LogDebug("液位报警状态: HighAlarm={HighAlarm}, HighWarning={HighWarning}, LowWarning={LowWarning}, LowAlarm={LowAlarm}",
                    highAlarm, highWarning, lowWarning, lowAlarm);

                if (highAlarm)
                {
                    _logger.LogError("🚨 液位过高报警!");

                    // 停止补液
                    await _businessService.WriteSingleRegisterAsync(
                        @event.ConnectionId,
                        "emulsion_master",
                        address: 500,  // 补液阀
                        value: 0,
                        cancellationToken);

                    // 打开排液阀
                    await _businessService.WriteSingleRegisterAsync(
                        @event.ConnectionId,
                        "emulsion_master",
                        address: 501,  // 排液阀
                        value: 1,
                        cancellationToken);

                    _logger.LogInformation("✅ 液位过高处理: 已停止补液, 排液阀已打开");
                }
                else if (lowAlarm)
                {
                    _logger.LogError("🚨 液位过低报警!");

                    // 打开补液阀
                    await _businessService.WriteSingleRegisterAsync(
                        @event.ConnectionId,
                        "emulsion_master",
                        address: 500,
                        value: 1,
                        cancellationToken);

                    // 关闭排液阀
                    await _businessService.WriteSingleRegisterAsync(
                        @event.ConnectionId,
                        "emulsion_master",
                        address: 501,
                        value: 0,
                        cancellationToken);

                    _logger.LogInformation("✅ 液位过低处理: 补液阀已打开, 排液阀已关闭");
                }
                else if (highWarning)
                {
                    _logger.LogWarning("⚠️ 液位高预警 - 降低补液速度");

                    await _businessService.WriteSingleRegisterAsync(
                        @event.ConnectionId,
                        "emulsion_master",
                        address: 502,  // 补液速度
                        value: 30,
                        cancellationToken);
                }
                else if (lowWarning)
                {
                    _logger.LogWarning("⚠️ 液位低预警 - 提高补液速度");

                    await _businessService.WriteSingleRegisterAsync(
                        @event.ConnectionId,
                        "emulsion_master",
                        address: 502,
                        value: 80,
                        cancellationToken);
                }
            }
        }

        /// <summary>
        /// 处理喷雾系统压力报警
        /// </summary>
        private async Task HandleAtomizingPressureWarningAsync(
            ParameterValueChangedEvent @event,
            CancellationToken cancellationToken)
        {
            _logger.LogInformation("🌫️ 处理喷雾系统压力报警: {ConnectionId}/{DeviceId}",
                @event.ConnectionId, @event.DeviceId);

            if (@event.DataPoint?.ParsedValue is Dictionary<string, bool> bitMap)
            {
                bool highAlarm = bitMap.GetValueOrDefault("atomizingMasterPressureAlarm_HighAlarm");
                bool lowAlarm = bitMap.GetValueOrDefault("atomizingMasterPressureAlarm_LowAlarm");

                if (highAlarm || lowAlarm)
                {
                    _logger.LogError("🚨 喷雾系统压力异常: HighAlarm={HighAlarm}, LowAlarm={LowAlarm}",
                        highAlarm, lowAlarm);

                    // 类似乳化系统的处理逻辑
                    // ...
                }
            }
        }

        /// <summary>
        /// 处理喷雾液箱液位报警
        /// </summary>
        private async Task HandleAtomizingWaterLevelWarningAsync(
            ParameterValueChangedEvent @event,
            CancellationToken cancellationToken)
        {
            _logger.LogInformation("💧 处理喷雾液箱液位报警: {ConnectionId}/{DeviceId}",
                @event.ConnectionId, @event.DeviceId);

            if (@event.DataPoint?.ParsedValue is Dictionary<string, bool> bitMap)
            {
                // 类似乳化液箱的处理逻辑
                // ...
            }
        }

        /// <summary>
        /// 通用泵报警状态处理
        /// </summary>
        private async Task HandlePumpWarningStateAsync(
            ParameterValueChangedEvent @event,
            CancellationToken cancellationToken)
        {
            _logger.LogInformation("🔧 处理泵报警状态: {ParamCode}", @event.ParameterCode);

            if (@event.DataPoint?.ParsedValue is Dictionary<string, bool> bitMap)
            {
                // 检查是否有任何报警触发
                bool hasAlarm = bitMap.Values.Any(v => v);

                if (hasAlarm)
                {
                    _logger.LogWarning("⚠️ 泵报警触发: {ParamCode}", @event.ParameterCode);

                    // 记录所有触发的位
                    foreach (var kvp in bitMap.Where(x => x.Value))
                    {
                        _logger.LogWarning("  - {BitName} = {Value}", kvp.Key, kvp.Value);
                    }

                    // 根据报警类型执行对应操作
                    if (@event.ParameterCode.Contains("OilPressure"))
                    {
                        _logger.LogError("🚨 润滑油压力异常 - 立即停泵保护");

                        // 停止泵运行
                        await _businessService.WriteSingleRegisterAsync(
                            @event.ConnectionId,
                            @event.DeviceId,
                            address: 80,  // 泵启动/停止地址
                            value: 0,
                            cancellationToken);
                    }
                    else if (@event.ParameterCode.Contains("Temperature"))
                    {
                        _logger.LogWarning("🌡️ 温度异常 - 降低负载");

                        // 降低转速
                        await _businessService.WriteSingleRegisterAsync(
                            @event.ConnectionId,
                            @event.DeviceId,
                            address: 301,
                            value: 60,
                            cancellationToken);
                    }
                }
                else
                {
                    _logger.LogInformation("✅ 泵报警已恢复正常: {ParamCode}", @event.ParameterCode);
                }
            }
        }

        private async Task HandleTemperatureChangeAsync(
            ParameterValueChangedEvent @event,
            CancellationToken cancellationToken)
        {
            if (@event.NewValue is double temperature && temperature > 80.0)
            {
                _logger.LogWarning("🌡️ 温度超限: {Temp}°C", temperature);

                var otherData = await _businessService.ReadRegistersAsync(
                    "plc_002",
                    "cooling_system",
                    cancellationToken);

                if (otherData != null)
                {
                    _logger.LogInformation("📖 读取到冷却系统数据: {Count} 个寄存器", otherData.Count);
                }

                bool success = await _businessService.WriteSingleRegisterAsync(
                    connectionId: "plc_002",
                    deviceId: "cooling_system",
                    address: 100,
                    value: 1,
                    cancellationToken);

                if (success)
                {
                    _logger.LogInformation("✅ 已启动冷却系统");
                }
            }
            else if (@event.NewValue is double temp && temp < 60.0)
            {
                await _businessService.WriteSingleRegisterAsync(
                    connectionId: "plc_002",
                    deviceId: "cooling_system",
                    address: 100,
                    value: 0,
                    cancellationToken);

                _logger.LogInformation("✅ 已关闭冷却系统");
            }
        }

        private async Task HandlePumpStatusChangeAsync(
            ParameterValueChangedEvent @event,
            CancellationToken cancellationToken)
        {
            _logger.LogInformation("⚙️ 泵状态变化: {OldStatus} → {NewStatus}",
                @event.OldValue, @event.NewValue);

            if (@event.NewValue?.ToString() == "故障")
            {
                _logger.LogWarning("⚠️ 主泵故障，启动备用泵");

                var backupData = await _businessService.ReadRegistersAsync(
                    "plc_001",
                    "backup_pump",
                    cancellationToken);

                if (backupData != null && backupData.TryGetValue(200, out var backupStatus))
                {
                    if (backupStatus == 0)
                    {
                        await _businessService.WriteSingleRegisterAsync(
                            connectionId: "plc_001",
                            deviceId: "backup_pump",
                            address: 201,
                            value: 1,
                            cancellationToken);

                        _logger.LogInformation("✅ 备用泵已启动");
                    }
                }
            }
        }

        private async Task HandlePressureChangeAsync(
            ParameterValueChangedEvent @event,
            CancellationToken cancellationToken)
        {
            if (@event.NewValue is double pressure)
            {
                if (pressure > 10.0)
                {
                    _logger.LogWarning("⚠️ 压力过高: {Pressure} bar", pressure);

                    await _businessService.WriteSingleRegisterAsync(
                        connectionId: @event.ConnectionId,
                        deviceId: @event.DeviceId,
                        address: 300,
                        value: 1,
                        cancellationToken);

                    await _businessService.WriteSingleRegisterAsync(
                        connectionId: @event.ConnectionId,
                        deviceId: @event.DeviceId,
                        address: 301,
                        value: 50,
                        cancellationToken);

                    _logger.LogInformation("✅ 已执行降压操作");
                }
                else if (pressure < 2.0)
                {
                    _logger.LogWarning("⚠️ 压力过低: {Pressure} bar", pressure);

                    await _businessService.WriteSingleRegisterAsync(
                        connectionId: @event.ConnectionId,
                        deviceId: @event.DeviceId,
                        address: 300,
                        value: 0,
                        cancellationToken);

                    await _businessService.WriteSingleRegisterAsync(
                        connectionId: @event.ConnectionId,
                        deviceId: @event.DeviceId,
                        address: 301,
                        value: 80,
                        cancellationToken);

                    _logger.LogInformation("✅ 已执行升压操作");
                }
            }
        }

        private async Task HandleFlowRateChangeAsync(
            ParameterValueChangedEvent @event,
            CancellationToken cancellationToken)
        {
            if (@event.NewValue is double flowRate)
            {
                _logger.LogInformation("💧 流量变化: {OldFlow} → {NewFlow} m³/h",
                    @event.OldValue, flowRate);

                if (flowRate > 100.0)
                {
                    _logger.LogWarning("⚠️ 流量过大: {FlowRate} m³/h", flowRate);

                    var valveData = await _businessService.ReadRegistersAsync(
                        "plc_001",
                        "main_valve",
                        cancellationToken);

                    if (valveData != null && valveData.TryGetValue(400, out var currentOpening))
                    {
                        var newOpening = (ushort)Math.Max(0, currentOpening - 10);

                        await _businessService.WriteSingleRegisterAsync(
                            connectionId: "plc_001",
                            deviceId: "main_valve",
                            address: 400,
                            value: newOpening,
                            cancellationToken);

                        _logger.LogInformation("✅ 阀门开度调整: {Old}% → {New}%",
                            currentOpening, newOpening);
                    }

                    await _businessService.WriteRegistersAsync(
                        connectionId: "plc_003",
                        deviceId: "downstream_device",
                        startAddress: 500,
                        values: new ushort[] { 1, (ushort)flowRate, 80 },
                        cancellationToken);

                    _logger.LogInformation("✅ 已调整下游设备参数");
                }
            }
        }

        private async Task HandleGenericAlarmAsync(
            ParameterValueChangedEvent @event,
            CancellationToken cancellationToken)
        {
            if (@event.NewValue is bool isAlarm && isAlarm)
            {
                _logger.LogError("🚨 告警触发: {AlarmCode} - {AlarmName}",
                    @event.ParameterCode, @event.ParameterName);

                var alarmData = await _businessService.ReadRegistersAsync(
                    @event.ConnectionId,
                    @event.DeviceId,
                    cancellationToken);

                if (alarmData != null)
                {
                    _logger.LogInformation("📖 告警上下文数据: {Count} 个寄存器", alarmData.Count);

                    if (alarmData.TryGetValue(600, out var alarmCode))
                    {
                        _logger.LogWarning("告警代码: 0x{Code:X4}", alarmCode);
                    }
                }

                await _businessService.WriteSingleRegisterAsync(
                    connectionId: @event.ConnectionId,
                    deviceId: @event.DeviceId,
                    address: 999,
                    value: 1,
                    cancellationToken);

                _logger.LogInformation("✅ 告警处理完成");
            }
        }
    }
}