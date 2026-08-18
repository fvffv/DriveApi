namespace drive_api.Services.TrafficStatistics
{
    public class TrafficStoreService
    {
        private long _todayUploadBytes = 0;
        private long _todayDownloadBytes = 0;


        // 供中间件调用：增加上传流量
        public void AddUpload(long bytes)
        {
            Interlocked.Add(ref _todayUploadBytes, bytes);
        }

        // 供中间件调用：增加下载流量
        public void AddDownload(long bytes)
        {
            Interlocked.Add(ref _todayDownloadBytes, bytes);
        }

        // 供后台任务调用：获取当前数值，并将其重置为 0
        public (long Upload, long Download) GetAndReset()
        {
            // Interlocked.Exchange 可以在读取当前值的同时将其设为新值(0)，绝对线程安全
            long currentUpload = Interlocked.Exchange(ref _todayUploadBytes, 0);
            long currentDownload = Interlocked.Exchange(ref _todayDownloadBytes, 0);

            return (currentUpload, currentDownload);
        }
    }
}
