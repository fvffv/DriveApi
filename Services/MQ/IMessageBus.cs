namespace drive_api.Services.MQ
{
    public interface IMessageBus
    {
        // 发布消息到指定主题
        Task PublishAsync<T>(string topic, T message);

        // 订阅主题，并设置并发处理任务数
        Task SubscribeAsync<T>(string topic, string subscriberName, Func<T, Task> handler, int concurrencyCount);
    }
}
