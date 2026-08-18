
using FreeRedis;

namespace drive_api.Services.Cache
{
    public class Redis : ICache
    {
        private readonly ILogger<Redis> _logger;
        public readonly RedisClient cli;
        public Redis(ILogger<Redis> logger, IConfiguration configuration)
        {
            _logger = logger;
            cli = new RedisClient(configuration.GetSection("CacheConfig").Get<CacheSetting>().ConnectionString);

            try
            {
                _logger.LogInformation("开始连接Redis...");
                cli.Ping();
                IsConnected = true;
                _logger.LogInformation("连接Redis成功");
                // 订阅事件
                cli.Connected += OnConnected;
                cli.Disconnected += OnDisconnected;
            }
            catch (Exception e)
            {
                IsConnected = false;
                _logger.LogError($"连接Redis失败: {e.Message}");
            }



        }
        public bool IsConnected { get; private set; }
        /// <summary>
        /// 组合缓存键
        /// </summary>
        /// <param name="cacheName"></param>
        /// <param name="key"></param>
        /// <returns></returns>
        private string GetCacheKey(string cacheName, string key)
        {

            return $"{cacheName}:{key}";

        }
        public Task<bool> ExistsAsync(string cacheName, string key)
        {
            return cli.ExistsAsync(GetCacheKey(cacheName, key));
        }

        public Task<T> GetAsync<T>(string cacheName, string key)
        {

            return cli.GetAsync<T>(GetCacheKey(cacheName, key));
        }

        public Task RemoveAsync(string cacheName, string key)
        {
            return cli.DelAsync(GetCacheKey(cacheName, key));
        }

        public Task SetAsync<T>(string cacheName, string key, T value, DateTimeOffset absoluteExpiration)
        {
            return cli.SetAsync(GetCacheKey(cacheName, key), value, (int)(absoluteExpiration - DateTimeOffset.Now).TotalSeconds);
        }

        private void OnConnected(object? sender, EventArgs e)
        {
            _logger.LogInformation("Redis连接成功！");
        }

        private void OnDisconnected(object? sender, DisconnectedEventArgs e)
        {
            _logger.LogWarning("Redis已断开");
        }


    }
}
