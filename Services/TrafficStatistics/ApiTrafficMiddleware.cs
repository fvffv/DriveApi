using drive_api.Services.Cache;
namespace drive_api.Services.TrafficStatistics
{

    public class ApiTrafficMiddleware(ICache icache, RequestDelegate next, TrafficStoreService trafficStore)
    {
        private readonly RequestDelegate _next = next;
        private readonly ICache _icache = icache;
        //流量记录服务
        private readonly TrafficStoreService _trafficStore = trafficStore;

        public async Task InvokeAsync(HttpContext context)
        {
            // 1. 获取当前请求匹配到的路由终结点 (Endpoint)
            var endpoint = context.GetEndpoint();

            // 2. 从终结点元数据中查找 [TrackTraffic] 特性
            var trackAttribute = endpoint?.Metadata.GetMetadata<TrackTrafficAttribute>();

            // 3. 如果没有贴标签，放行
            if (trackAttribute == null)
            {
                await _next(context);
                return;
            }


            var originalRequestStream = context.Request.Body;
            var originalResponseStream = context.Response.Body;

            //TrackingStream 包装原始流
            using var trackingRequestStream = new TrackingStream(originalRequestStream);
            using var trackingResponseStream = new TrackingStream(originalResponseStream);

            context.Request.Body = trackingRequestStream;
            context.Response.Body = trackingResponseStream;

            try
            {
                // 让管道继续执行后续的控制器代码
                await _next(context);
            }
            finally
            {
                // 在退出时恢复原始流，防止框架底层崩溃
                context.Request.Body = originalRequestStream;
                context.Response.Body = originalResponseStream;

                // 获取统计到的精准字节数
                long uploadBytes = trackingResponseStream.TotalWritten;
                long downloadBytes = trackingRequestStream.TotalRead;

                // 将流量数据记录到 TrafficStoreService 中
                if (uploadBytes > 0) _trafficStore.AddUpload(uploadBytes);
                if (downloadBytes > 0) _trafficStore.AddDownload(downloadBytes);
            }
        }
    }
}
