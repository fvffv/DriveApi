using drive_api.Models;
using drive_api.Services.AI;
using SqlSugar;
namespace drive_api.Services.MQ
{
    public class ImgVectorModel
    {
        public DbType DbType { get; set; }
        public string StoragePath { get; set; }
        public Guid UserId { get; set; }
        public Guid FileId { get; set; }
    }


    public class ImgVectorConsumer(ImgAiVectorService avs, IMessageBus messageBus, ILogger<ImgVectorConsumer> logger, IWebHostEnvironment env, IServiceScopeFactory scopeFactory) : BackgroundService
    {

        private readonly IMessageBus _messageBus = messageBus;
        private readonly ILogger<ImgVectorConsumer> _logger = logger;
        private readonly IWebHostEnvironment _env = env;
        private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
        private readonly ImgAiVectorService _avs = avs;

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await _messageBus.SubscribeAsync<ImgVectorModel>(
                topic: "ImgVector",
                subscriberName: "ImgVector",
                handler: HandleMessageAsync,
                concurrencyCount: 2  // 这里设置并发数！
            );
        }



        // 具体的业务处理逻辑
        private async Task HandleMessageAsync(ImgVectorModel message)
        {
            _logger.LogInformation($"[线程 {Thread.CurrentThread.ManagedThreadId}] 开始获取图片向量: {message.StoragePath}");
            float[] imageFeature = avs.GetImageFeature(message.StoragePath);
            using (var scope = _scopeFactory.CreateScope())
            {
                // 从当前独立的作用域中获取 ISqlSugarClient
                var sqlSugarClient = scope.ServiceProvider.GetRequiredService<ISqlSugarClient>();
                if (message.DbType == DbType.Sqlite)
                {
                    await sqlSugarClient.Ado.ExecuteCommandAsync(
                        """
                        INSERT INTO img_vectorcs(
                            file_id,
                            user_id,
                            vector
                        )
                        VALUES(
                            @fileId,
                            @userId,
                            vec_f32(@vector)
                        );
                        """,
                        new
                        {
                            fileId = message.FileId.ToString("D"),
                            userId = message.UserId.ToString("D"),
                            vector = System.Text.Json.JsonSerializer.Serialize(imageFeature)
                        });
                }
                else
                {
                    await sqlSugarClient.Insertable(new ImgVectorcs
                    {
                        FileId = message.FileId,
                        Vector = imageFeature,
                        UserId = message.UserId
                    }).ExecuteCommandAsync();
                }
            }

            logger.LogInformation($"图片向量 {message.FileId} 处理完成！");
        }


    }
}
