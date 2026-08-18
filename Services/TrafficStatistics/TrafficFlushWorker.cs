using drive_api.Models;
using drive_api.Services.TrafficStatistics;
using SqlSugar;

/// <summary>
/// 流量统计定时持久化后台工作服务
/// 负责周期性将内存中的上下行流量统计数据（增量）同步保存至数据库
/// </summary>
public class TrafficFlushWorker(TrafficStoreService trafficStore, IServiceScopeFactory scopeFactory, ILogger<TrafficFlushWorker> logger) : BackgroundService
{
    // 内存中的流量计数服务，负责暂存并原子操作获取最新的流量增量
    private readonly TrafficStoreService _trafficStore = trafficStore;

    //BackgroundService 
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;

    // 日志
    private readonly ILogger<TrafficFlushWorker> _logger = logger;

    /// <summary>
    /// 后台服务执行的主循环方法
    /// </summary>
    /// <param name="stoppingToken">应用停止时的取消令牌</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("流量统计持久化后台任务已启动...");

        // ========================== 1. 初始化阶段 ==========================
        // 启动时检查并确保数据库中存在当天（DateTime.Today）的统计初始记录
        using (IServiceScope scope = _scopeFactory.CreateScope())
        {
            ISqlSugarClient requiredService = scope.ServiceProvider.GetRequiredService<ISqlSugarClient>();

            // 查询当天是否已经有流量记录
            TrafficStatisticsModel trafficStatisticsModel = requiredService.Queryable<TrafficStatisticsModel>()
                .First((TrafficStatisticsModel x) => x.Date == DateTime.Today);

            // 如果当天还未生成记录，则向数据库插入一条初始值为 0 的记录
            if (trafficStatisticsModel == null)
            {
                trafficStatisticsModel = new TrafficStatisticsModel
                {
                    Id = Guid.NewGuid(),
                    Date = DateTime.Today,
                    UploadBytes = 0L,
                    DownloadBytes = 0L
                };
                await requiredService.Insertable(trafficStatisticsModel).ExecuteCommandAsync();
            }
        }

        // ========================== 2. 周期性持久化阶段 ==========================
        // 只要服务未收到停止请求，就持续轮询执行
        while (!stoppingToken.IsCancellationRequested)
        {
            // 每隔 30 秒执行一次持久化操作
            await Task.Delay(TimeSpan.FromSeconds(30L), stoppingToken);

            // 从内存服务中取出自上次清空以来的上行与下行流量增量，并将内存计数器重置为 0
            var (upload, download) = _trafficStore.GetAndReset();

            // 如果这一周期内没有任何上下行流量发生，则直接进入下一轮循环，避免无效操作数据库
            if (upload == 0L && download == 0L)
            {
                continue;
            }

            try
            {
                // 创建全新的作用域解析数据库上下文（确保线程安全且防连接泄漏）
                using (IServiceScope scope = _scopeFactory.CreateScope())
                {
                    ISqlSugarClient requiredService2 = scope.ServiceProvider.GetRequiredService<ISqlSugarClient>();

                    // 重新获取当天的记录（考虑跨日情况，DateTime.Today 在午夜后会变化）
                    TrafficStatisticsModel todayRecord = requiredService2.Queryable<TrafficStatisticsModel>()
                        .First((TrafficStatisticsModel x) => x.Date == DateTime.Today);

                    // 如果出现跨日（例如刚过 0 点），新的一天数据库中还没有记录，则直接新建记录并将本次增量写入
                    if (todayRecord == null)
                    {
                        todayRecord = new TrafficStatisticsModel
                        {
                            Id = Guid.NewGuid(),
                            Date = DateTime.Today,
                            UploadBytes = upload,
                            DownloadBytes = download
                        };
                        await requiredService2.Insertable(todayRecord).ExecuteCommandAsync();
                    }
                    else
                    {
                        // 如果当天记录存在，累加本次周期内的增量数据
                        todayRecord.UploadBytes += upload;
                        todayRecord.DownloadBytes += download;

                        // 指定部分列进行更新操作，持久化最新的累计流量值
                        await (from it in requiredService2.Updateable<TrafficStatisticsModel>()
                               .SetColumns((TrafficStatisticsModel it) => new TrafficStatisticsModel
                               {
                                   UploadBytes = todayRecord.UploadBytes,
                                   DownloadBytes = todayRecord.DownloadBytes
                               })
                               where it.Id == todayRecord.Id
                               select it).ExecuteCommandAsync();
                    }
                }

                _logger.LogInformation($"定时上传流量日志，将 {upload} 字节上传, {download} 字节下载，同步到数据库。");
            }
            catch (Exception exception)
            {

                // 如果数据库网络异常或写入报错，记录异常信息
                _logger.LogError(exception, "持久化流量数据到数据库时发生异常！");

                // 将已经取出来的流量增量重新放回内存计数器，确保数据在下一次轮询时继续尝试保存而不丢失
                _trafficStore.AddUpload(upload);
                _trafficStore.AddDownload(download);
            }
        }
    }
}