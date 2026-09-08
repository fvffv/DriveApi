using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
namespace drive_api.Services.MQ
{
    public class ImgCompModel
    {
        public string StoragePath { get; set; }
        public string FileHash { get; set; }
    }
    public class ImgCompConsumer(IMessageBus messageBus, ILogger<ImgCompConsumer> logger, IWebHostEnvironment env) : BackgroundService
    {

        private readonly IMessageBus _messageBus = messageBus;
        private readonly ILogger<ImgCompConsumer> _logger = logger;
        private readonly IWebHostEnvironment _env = env;


        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // 订阅主题 "file.uploaded"
            // 针对这个消费者，限制同时最多有 5 个任务在处理 (控制并发)
            await _messageBus.SubscribeAsync<ImgCompModel>(
                topic: "ImgComp",
                subscriberName: "ImgComp",
                handler: HandleMessageAsync,
                concurrencyCount: 3  // 这里设置并发数
            );
        }

        // 具体的业务处理逻辑
        private async Task HandleMessageAsync(ImgCompModel message)
        {
            _logger.LogInformation($"[线程 {Thread.CurrentThread.ManagedThreadId}] 开始处理图片缩略图: {message.StoragePath}");
            //压缩图片并保存到指定路径，命名为原文件hash值.jpg
            CreateThumbnail(message.StoragePath, Path.Combine(_env.WebRootPath, "driveassets/imgcomp", message.FileHash + ".jpg"), 100, 100);

            _logger.LogInformation($"图片缩略图 {message.StoragePath} 处理完成！");
        }

        private void CreateThumbnail(string inputPath, string outputPath, int width, int height)
        {
            // 自动识别图片格式并加载
            using (var image = Image.Load(inputPath))
            {
                // Mutate 表示就地修改图片
                image.Mutate(x => x.Resize(new ResizeOptions
                {
                    Size = new Size(width, height),
                    Mode = ResizeMode.Crop // 裁剪模式：保持比例，裁剪掉多余部分
                }));

                image.Save(outputPath); // 根据后缀自动推断保存格式
            }
        }
    }
}
