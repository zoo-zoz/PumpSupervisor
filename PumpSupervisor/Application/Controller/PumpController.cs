using Microsoft.Extensions.Logging;
using PumpSupervisor.Application.Services;

namespace PumpSupervisor.Application.Controller
{
    public class PumpController
    {
        private readonly IModbusApiService _modbusApi;
        private readonly ILogger<PumpController> _logger;

        public PumpController(
            IModbusApiService modbusApi,
            ILogger<PumpController> logger)
        {
            _modbusApi = modbusApi;
            _logger = logger;
        }

        //// 按需读取设备数据
        //public async Task<IActionResult> ReadDeviceData(
        //    string connectionId,
        //    string deviceId)
        //{
        //    var result = await _modbusApi.ReadDeviceDataAsync(connectionId, deviceId);

        //    if (result.Success)
        //    {
        //        return Ok(new { success = true, data = result.Data });
        //    }

        //    return BadRequest(new { success = false, message = result.Message });
        //}

        //// 启动泵（写入控制寄存器）
        //public async Task<IActionResult> StartPump(
        //    string connectionId,
        //    string deviceId)
        //{
        //    // 写入启动命令到地址80，值为1
        //    var result = await _modbusApi.WriteSingleRegisterAsync(
        //        connectionId,
        //        deviceId,
        //        80,
        //        1);

        //    if (result.Success)
        //    {
        //        _logger.LogInformation("泵启动成功: {DeviceId}", deviceId);
        //        return Ok(new { success = true, message = "泵启动成功" });
        //    }

        //    return BadRequest(new { success = false, message = result.Message });
        //}

        //// 停止泵
        //public async Task<IActionResult> StopPump(
        //    string connectionId,
        //    string deviceId)
        //{
        //    // 写入停止命令到地址81，值为1
        //    var result = await _modbusApi.WriteSingleRegisterAsync(
        //        connectionId,
        //        deviceId,
        //        81,
        //        1);

        //    return result.Success
        //        ? Ok(new { success = true, message = "泵停止成功" })
        //        : BadRequest(new { success = false, message = result.Message });
        //}

        //// 设置参数
        //public async Task<IActionResult> SetParameter(
        //    string connectionId,
        //    string deviceId,
        //    int address,
        //    float value)
        //{
        //    // 将float转换为Modbus寄存器值（需要根据实际协议）
        //    var bytes = BitConverter.GetBytes(value);
        //    var registers = new ushort[]
        //    {
        //    BitConverter.ToUInt16(bytes, 0),
        //    BitConverter.ToUInt16(bytes, 2)
        //    };

        //    var result = await _modbusApi.WriteDataAsync(
        //        connectionId,
        //        deviceId,
        //        address,
        //        registers);

        //    return result.Success
        //        ? Ok(new { success = true, message = "参数设置成功" })
        //        : BadRequest(new { success = false, message = result.Message });
        //}
    }
}