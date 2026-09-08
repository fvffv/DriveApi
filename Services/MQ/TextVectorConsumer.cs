using DocumentFormat.OpenXml.Spreadsheet;
using drive_api.Models;
using drive_api.Services.AI;
using drive_api.Services.Tool;
using Serilog.Core;
using SqlSugar;

namespace drive_api.Services.MQ
{
    public class TextVectorModel
    {
        public DbType DbType { get; set; }
        public string StoragePath { get; set; }
        public Guid UserId { get; set; }
        public Guid FileId { get; set; }
        public string FileName { get; set; }
    }
    public class TextVectorConsumer(TextAiVectorService avs, IMessageBus messageBus, ILogger<ImgCompConsumer> logger, IWebHostEnvironment env, IServiceScopeFactory scopeFactory) : BackgroundService
    {
        private readonly IMessageBus _messageBus = messageBus;
        private readonly ILogger<ImgCompConsumer> _logger = logger;
        private readonly IWebHostEnvironment _env = env;
        private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
        private readonly TextAiVectorService _avs = avs;
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await _messageBus.SubscribeAsync<TextVectorModel>(
                topic: "TextVector",
                subscriberName: "TextVector",
                handler: HandleMessageAsync,
                concurrencyCount: 3  // 这里设置并发数！
            );
        }


        // 具体的业务处理逻辑
        private async Task HandleMessageAsync(TextVectorModel message)
        {
            _logger.LogInformation($"[线程 {Thread.CurrentThread.ManagedThreadId}] 开始计算文本向量: {message.StoragePath}");
            IReadOnlyList<TextVectorChunk> textFeature = null;
            //判断是否是文档类型，如果是文档类型，先提取文本内容，再计算向量
            if (Helper.DocExtensions.Contains(Path.GetExtension(message.FileName)))
            {
                textFeature = avs.GetTextFeatureFromText(Helper.ExtractAllText(message.StoragePath, message.FileName));
            }
            else
            {
                textFeature = avs.GetTextFeatureFromFile(message.StoragePath);
            }

            if (textFeature is { Count: > 0 })
            {
                using var scope = _scopeFactory.CreateScope();
                var sqlSugarClient = scope.ServiceProvider
                    .GetRequiredService<ISqlSugarClient>();


                if (message.DbType == DbType.Sqlite)
                {
                    foreach (TextVectorChunk chunk in textFeature)
                    {
                        await sqlSugarClient.Ado.ExecuteCommandAsync(
                             """
                            INSERT INTO text_vectorcs(
                                file_id,
                                user_id,
                                chunk_id,
                                vector
                            )
                            VALUES(
                                @fileId,
                                @userId,
                                @chunkId,
                                vec_f32(@vector)
                            );
                            """,
                            new
                            {
                                fileId = message.FileId.ToString("D"),
                                userId = message.UserId.ToString("D"),
                                chunkId = chunk.Index,
                                vector = System.Text.Json.JsonSerializer.Serialize(chunk.Vector)
                            });
                    }
                }
                else
                {
                    await sqlSugarClient.Insertable(textFeature.Select(x => new TextVectorcs
                    {
                        ChunkId = x.Index,
                        FileId = message.FileId,
                        Vector = x.Vector,
                        UserId = message.UserId
                    }).ToArray()).ExecuteCommandAsync();
                }

              
            }

            logger.LogInformation("文本向量 {FileId} 处理完成！", message.FileId);
        }
    }

}
