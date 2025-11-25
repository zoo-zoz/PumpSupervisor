using System.Threading.Channels;

namespace PumpSupervisor.Infrastructure.Modbus
{
    public interface IModbusCommandQueue
    {
        Task EnqueueAsync<T>(T command, int priority, CancellationToken cancellationToken = default)
            where T : class;

        Task<(object Command, int Priority)> DequeueAsync(CancellationToken cancellationToken = default);
    }

    public class PriorityModbusCommandQueue : IModbusCommandQueue
    {
        private readonly Channel<(object Command, int Priority)> _channel;
        private readonly SemaphoreSlim _lock = new(1, 1);
        private readonly List<(object Command, int Priority)> _buffer = new();

        public PriorityModbusCommandQueue()
        {
            _channel = Channel.CreateUnbounded<(object, int)>();
        }

        public async Task EnqueueAsync<T>(T command, int priority, CancellationToken cancellationToken = default)
            where T : class
        {
            await _lock.WaitAsync(cancellationToken);
            try
            {
                _buffer.Add((command, priority));

                // 按优先级排序（高优先级在前）
                _buffer.Sort((a, b) => b.Priority.CompareTo(a.Priority));

                // 将排序后的命令写入Channel
                if (_buffer.Count > 0)
                {
                    var item = _buffer[0];
                    _buffer.RemoveAt(0);
                    await _channel.Writer.WriteAsync(item, cancellationToken);
                }
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task<(object Command, int Priority)> DequeueAsync(CancellationToken cancellationToken = default)
        {
            return await _channel.Reader.ReadAsync(cancellationToken);
        }
    }
}