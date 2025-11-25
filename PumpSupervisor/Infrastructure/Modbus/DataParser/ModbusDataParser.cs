using Microsoft.Extensions.Logging;
using PumpSupervisor.Domain.Models;
using System.Text;

namespace PumpSupervisor.Infrastructure.Modbus.DataParser
{
    public interface IModbusDataParser
    {
        object ParseValue(
            ushort[] registers,
            int startIndex,
            string dataType,
            string byteOrder,
            double scale = 1.0,
            double offset = 0.0);

        Dictionary<string, bool> ParseBitMap(
            ushort value,
            Dictionary<string, BitMapItem> bitMap);
    }

    public class ModbusDataParser : IModbusDataParser
    {
        private readonly ILogger<ModbusDataParser>? _logger;

        public ModbusDataParser(ILogger<ModbusDataParser>? logger = null)
        {
            _logger = logger;
        }

        public object ParseValue(
            ushort[] registers,
            int startIndex,
            string dataType,
            string byteOrder,
            double scale = 1.0,
            double offset = 0.0)
        {
            return dataType.ToLower() switch
            {
                "bit" => (registers[startIndex] & 0x01) == 1,
                "uint16" => (ushort)(registers[startIndex] * scale + offset),
                "int16" => (short)(registers[startIndex] * scale + offset),
                "uint32" => ParseUInt32(registers, startIndex, byteOrder, scale, offset),
                "int32" => ParseInt32(registers, startIndex, byteOrder, scale, offset),
                "float32" => ParseFloat32(registers, startIndex, byteOrder, scale, offset),
                "string" => ParseString(registers, startIndex),
                _ => throw new ArgumentException($"不支持的数据类型: {dataType}")
            };
        }

        private uint ParseUInt32(ushort[] registers, int startIndex, string byteOrder, double scale, double offset)
        {
            _logger?.LogDebug(
                "🔢 ParseUInt32 输入 - StartIndex={StartIndex}, Reg0=0x{Reg0:X4}, Reg1=0x{Reg1:X4}, ByteOrder={ByteOrder}",
                startIndex, registers[startIndex], registers[startIndex + 1], byteOrder
            );

            var bytes = GetBytesFromRegisters(registers, startIndex, 2, byteOrder);

            _logger?.LogDebug(
                "📦 字节数组 - [{Bytes}]",
                string.Join(", ", bytes.Select(b => $"0x{b:X2}"))
            );

            // 需要根据字节序决定是否反转
            if (!BitConverter.IsLittleEndian)
            {
                Array.Reverse(bytes);
            }

            var uintValue = BitConverter.ToUInt32(bytes, 0);
            var result = (uint)(uintValue * scale + offset);

            _logger?.LogDebug(
                "✅ ParseUInt32 输出 - UInt={UInt}, Scaled={Result}",
                uintValue, result
            );

            return result;
        }

        private int ParseInt32(ushort[] registers, int startIndex, string byteOrder, double scale, double offset)
        {
            _logger?.LogDebug(
                "🔢 ParseInt32 输入 - StartIndex={StartIndex}, Reg0=0x{Reg0:X4}, Reg1=0x{Reg1:X4}, ByteOrder={ByteOrder}",
                startIndex, registers[startIndex], registers[startIndex + 1], byteOrder
            );

            var bytes = GetBytesFromRegisters(registers, startIndex, 2, byteOrder);

            _logger?.LogDebug(
                "📦 字节数组 - [{Bytes}]",
                string.Join(", ", bytes.Select(b => $"0x{b:X2}"))
            );

            if (!BitConverter.IsLittleEndian)
            {
                Array.Reverse(bytes);
            }

            var intValue = BitConverter.ToInt32(bytes, 0);
            var result = (int)(intValue * scale + offset);

            _logger?.LogDebug(
                "✅ ParseInt32 输出 - Int={Int}, Scaled={Result}",
                intValue, result
            );

            return result;
        }

        private double ParseFloat32(ushort[] registers, int startIndex, string byteOrder, double scale, double offset)
        {
            _logger?.LogDebug(
                "🔢 ParseFloat32 输入 - StartIndex={StartIndex}, Reg0=0x{Reg0:X4}, Reg1=0x{Reg1:X4}, ByteOrder={ByteOrder}",
                startIndex, registers[startIndex], registers[startIndex + 1], byteOrder
            );

            var bytes = GetBytesFromRegisters(registers, startIndex, 2, byteOrder);

            _logger?.LogDebug(
                "📦 字节数组 - [{Bytes}]",
                string.Join(", ", bytes.Select(b => $"0x{b:X2}"))
            );

            if (!BitConverter.IsLittleEndian)
            {
                Array.Reverse(bytes);
            }

            var floatValue = BitConverter.ToSingle(bytes, 0);
            var result = floatValue * scale + offset;

            _logger?.LogDebug(
                "✅ ParseFloat32 输出 - Float={Float}, Scaled={Result}",
                floatValue, result
            );

            return result;
        }

        private string ParseString(ushort[] registers, int startIndex)
        {
            var bytes = new List<byte>();
            for (int i = startIndex; i < registers.Length; i++)
            {
                bytes.Add((byte)(registers[i] >> 8));
                bytes.Add((byte)(registers[i] & 0xFF));
            }

            return Encoding.ASCII.GetString(bytes.ToArray()).TrimEnd('\0');
        }

        private byte[] GetBytesFromRegisters(ushort[] registers, int startIndex, int count, string byteOrder)
        {
            var bytes = new byte[count * 2];

            // 先按Modbus协议的Big Endian提取每个寄存器的字节
            for (int i = 0; i < count; i++)
            {
                var reg = registers[startIndex + i];
                bytes[i * 2] = (byte)(reg >> 8);      // 高字节
                bytes[i * 2 + 1] = (byte)(reg & 0xFF); // 低字节
            }

            // 仅对32位类型（count==2）进行字节序转换
            if (count != 2)
            {
                return bytes;
            }

            // 根据配置的字节序进行转换
            // 注意：最终结果需要匹配 BitConverter 的 Little Endian 格式
            return byteOrder.ToUpper() switch
            {
                // ABCD: Modbus 标准 Big Endian [高字, 低字]
                // 对于 Little Endian 系统，需要完全反转
                "ABCD" => ReverseBytes(bytes),        // [A,B,C,D] → [D,C,B,A] for LE

                // DCBA: 完全反转的 Big Endian
                // 对于 Little Endian 系统，刚好不用转换
                "DCBA" => bytes,                       // [D,C,B,A] 已经是 LE 格式

                // BADC: 字内字节互换
                // 对于 Little Endian 系统，需要交换字序
                "BADC" => SwapWords(SwapWordBytes(bytes)),  // [B,A,D,C] → [C,D,A,B] → [B,A,D,C]

                // CDAB: 字序互换
                // 对于 Little Endian 系统，只需交换字序后再反转
                "CDAB" => SwapWordBytes(bytes),        // [C,D,A,B] → [D,C,B,A]

                _ => ReverseBytes(bytes)  // 默认按 ABCD 处理
            };
        }

        private byte[] ReverseBytes(byte[] bytes)
        {
            var result = new byte[bytes.Length];
            for (int i = 0; i < bytes.Length; i++)
            {
                result[i] = bytes[bytes.Length - 1 - i];
            }
            return result;
        }

        private byte[] SwapWordBytes(byte[] bytes)
        {
            // [A, B, C, D] -> [B, A, D, C]
            var result = new byte[bytes.Length];
            for (int i = 0; i < bytes.Length; i += 2)
            {
                result[i] = bytes[i + 1];
                result[i + 1] = bytes[i];
            }
            return result;
        }

        private byte[] SwapWords(byte[] bytes)
        {
            // [A, B, C, D] -> [C, D, A, B]
            var result = new byte[4];
            result[0] = bytes[2];
            result[1] = bytes[3];
            result[2] = bytes[0];
            result[3] = bytes[1];
            return result;
        }

        public Dictionary<string, bool> ParseBitMap(ushort value, Dictionary<string, BitMapItem> bitMap)
        {
            var result = new Dictionary<string, bool>();

            foreach (var kvp in bitMap)
            {
                var bitIndex = int.Parse(kvp.Key);
                var bitValue = (value & (1 << bitIndex)) != 0;
                result[kvp.Value.Code] = bitValue;
            }

            return result;
        }
    }
}