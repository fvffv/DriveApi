using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
namespace drive_api.Services.MQ
{
    public class InMemoryMessageBus : IMessageBus
    {
        // 存储主题和对应的无界队列
        private readonly ConcurrentDictionary<string, Channel<string>> _channels = new();

        public async Task PublishAsync<T>(string topic, T message)
        {
            var json = JsonSerializer.Serialize(message);
            var channel = _channels.GetOrAdd(topic, _ => Channel.CreateUnbounded<string>());
            await channel.Writer.WriteAsync(json);
        }

        public Task SubscribeAsync<T>(string topic, string subscriberName, Func<T, Task> handler, int concurrencyCount)
        {
            // 注意：内存模式下 subscriberName 作用不大，主要为了兼容接口
            var channel = _channels.GetOrAdd(topic, _ => Channel.CreateUnbounded<string>());

            // 核心：启动指定数量 (concurrencyCount) 的长任务来读取同一个队列
            for (int i = 0; i < concurrencyCount; i++)
            {
                _ = Task.Run(async () =>
                {
                    await foreach (var json in channel.Reader.ReadAllAsync())
                    {
                        try
                        {
                            var message = JsonSerializer.Deserialize<T>(json);
                            if (message != null)
                            {
                                await handler(message);
                            }
                        }
                        catch (Exception ex)
                        {
                            // 记录日志，防止 Task 崩溃
                            Console.WriteLine($"[InMemoryBus] 消费异常: {ex.Message}");
                        }
                    }
                });
            }

            return Task.CompletedTask;
        }
    }

}
