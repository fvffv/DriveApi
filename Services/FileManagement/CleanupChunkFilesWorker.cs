using drive_api.Services.Config;
using Serilog.Core;
using System.Text.Json;

namespace drive_api.Services.FileManagement
{
    public class CleanupChunkFilesWorker(ILogger<CleanupChunkFilesWorker> logger, AppConfigInfo appConfigInfo) : BackgroundService
    {
        private readonly ILogger<CleanupChunkFilesWorker> _logger = logger;
        private readonly AppConfigInfo _appConfigInfo = appConfigInfo;


        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation($"清理超时分片任务后台任务已启动,循环检测时间: {_appConfigInfo.fileSetting.CleanupChunkFilesWorkerLoopTimeSecond}秒,分片超时清理时间: {_appConfigInfo.fileSetting.CleanupChunkFilesWorkerTimeOutSecond}秒");
            var path = Path.Combine(_appConfigInfo.fileSetting.TempFilePath, "Temp", "ChunkData");
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_appConfigInfo.fileSetting.CleanupChunkFilesWorkerLoopTimeSecond));

           
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    // 枚举子目录
                    var dirs = Directory.EnumerateDirectories(path);
                    foreach (var dir in dirs) 
                    {
                        
                       string filePath = Path.Combine(dir, "upload_task.json");
                       // 超时清理
                       if (DateTimeOffset.FromUnixTimeSeconds(GetCreatedAtFast(filePath)).AddSeconds(_appConfigInfo.fileSetting.CleanupChunkFilesWorkerTimeOutSecond) < DateTimeOffset.Now)
                       {
                          Directory.Delete(dir, true);
                          _logger.LogInformation($"文件分块目录超时清理：{dir}");
                        }

                    }
                   

                }
                catch (Exception ex)
                {
                    
                    _logger.LogError(ex, "清理任务发生异常");
                }
            }
        }

        /// <summary>
        /// 极限性能读取 JSON 中的 created_at 时间戳
        /// </summary>
        public static long GetCreatedAtFast(string filePath)
        {
            if (!File.Exists(filePath)) return 0;

            // 1. 直接读取为字节数组，彻底跳过 string 转换和 UTF-16 编码带来的内存开销
            byte[] jsonBytes = File.ReadAllBytes(filePath);

            // 2. 实例化 Utf8JsonReader (它是 ref struct，完全分配在栈上，方法一结束立刻销毁)
            var reader = new Utf8JsonReader(jsonBytes);

            // 3. 预定义要查找的 Key。使用 "..."u8 是 C# 11 特性，在编译期直接变成只读的字节范围，0 内存分配！
            ReadOnlySpan<byte> targetKey = "created_at"u8;

            // 4. 开始流式向后扫描
            while (reader.Read())
            {
                // 当扫描到属性名（Key），且字节完全匹配时
                if (reader.TokenType == JsonTokenType.PropertyName && reader.ValueTextEquals(targetKey))
                {
                    // 指针再往前走一步，到达 Value
                    reader.Read();

                    // 直接提取 long 数字
                    return reader.GetInt64();
                }
            }

            return 0; // 没找到
        }
    }
}
