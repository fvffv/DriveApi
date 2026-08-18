using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System.Text;
using System.Text.Json;
namespace drive_api.Services.MQ
{
    public class RabbitMQMessageBus : IMessageBus, IDisposable
    {
        private readonly IConnection _connection;
        private const string ExchangeName = "cloud_drive_topic_exchange"; // 统一的交换机名称

        public RabbitMQMessageBus(string connectionString)
        {
            var factory = new ConnectionFactory
            {
                Uri = new Uri(connectionString),
            };

            _connection = factory.CreateConnectionAsync().GetAwaiter().GetResult();
        }

        public async Task PublishAsync<T>(string topic, T message)
        {
            using var channel = await _connection.CreateChannelAsync();

            // 声明 Topic 交换机
            await channel.ExchangeDeclareAsync(ExchangeName, ExchangeType.Topic, durable: true);

            var json = JsonSerializer.Serialize(message);
            var body = Encoding.UTF8.GetBytes(json);

            // 发布消息到交换机，以 topic 作为 routingKey
            await channel.BasicPublishAsync(ExchangeName, topic, body: body);
        }

        public async Task SubscribeAsync<T>(string topic, string subscriberName, Func<T, Task> handler, int concurrencyCount)
        {
            // 注意：消费者需要保持 Channel 打开，所以不能用 using
            var channel = await _connection.CreateChannelAsync();

            await channel.ExchangeDeclareAsync(ExchangeName, ExchangeType.Topic, durable: true);

            // 声明当前消费者的专属队列
            var queueName = $"queue_{subscriberName}_{topic}";
            await channel.QueueDeclareAsync(queueName, durable: true, exclusive: false, autoDelete: false);

            // 将队列绑定到 Topic 交换机 (可以使用通配符，如 "file.*"，这里使用精确匹配 topic)
            await channel.QueueBindAsync(queueName, ExchangeName, routingKey: topic);

            // 核心并发控制：PrefetchCount 预取数量
            await channel.BasicQosAsync(prefetchSize: 0, prefetchCount: (ushort)concurrencyCount, global: false);

            var consumer = new AsyncEventingBasicConsumer(channel);
            consumer.ReceivedAsync += async (model, ea) =>
            {
                try
                {
                    var body = ea.Body.ToArray();
                    var json = Encoding.UTF8.GetString(body);
                    var message = JsonSerializer.Deserialize<T>(json);

                    if (message != null)
                    {
                        // 执行业务逻辑 (根据传入的并发数，这里会多线程并发执行)
                        await handler(message);
                    }

                    // 手动确认 (Ack)
                    await channel.BasicAckAsync(ea.DeliveryTag, multiple: false);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[RabbitMQ] 消费异常: {ex.Message}");
                    // 处理失败，拒绝消息并重新入队 (Nack)
                    await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: true);
                }
            };

            // 启动消费，关闭自动确认
            await channel.BasicConsumeAsync(queueName, autoAck: false, consumer: consumer);
        }

        public void Dispose() => _connection?.Dispose();
    }
}
