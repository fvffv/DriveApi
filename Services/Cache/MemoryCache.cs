using Microsoft.Extensions.Caching.Memory; // 【核心替换】：引入现代内存缓存

namespace drive_api.Services.Cache
{
    public class MemoryCache : ICache
    {
        private readonly ILogger<MemoryCache> _logger;

        // 【核心替换】：使用依赖注入的接口，而不是自己 new
        private readonly IMemoryCache _caches;

        public MemoryCache(ILogger<MemoryCache> logger, IMemoryCache memoryCache)
        {
            _logger = logger;
            _caches = memoryCache;

            _logger.LogInformation("MemoryCache 初始化完成");
        }

        /// <summary>
        /// 获取缓存实例 (拼接Key)
        /// </summary>
        private string GetCache(string cacheName, string key)
        {
            return $"{cacheName}:{key}";
        }

        public Task<bool> ExistsAsync(string cacheName, string key)
        {
            // 现代版判断存在的方法：TryGetValue 返回 bool
            bool exists = _caches.TryGetValue(GetCache(cacheName, key), out _);
            return Task.FromResult(exists);
        }

        public Task<T> GetAsync<T>(string cacheName, string key)
        {
            // 1 & 2. 尝试获取值并检查是否为空
            if (!_caches.TryGetValue(GetCache(cacheName, key), out object value) || value == null)
            {
                // 如果缓存中没有，返回默认值 (null)
                return Task.FromResult(default(T));
            }

            // 3. 尝试将 object 转换为 T
            try
            {
                var result = (T)value;
                return Task.FromResult<T?>(result);
            }
            catch (InvalidCastException)
            {
                _logger.LogWarning($"缓存{cacheName}的key: '{key}'转换失败.");
                return Task.FromResult(default(T));
            }
        }

        public Task RemoveAsync(string cacheName, string key)
        {
            _caches.Remove(GetCache(cacheName, key));
            return Task.CompletedTask;
        }

        public Task SetAsync<T>(string cacheName, string key, T value, DateTimeOffset absoluteExpiration)
        {
            _caches.Set(GetCache(cacheName, key), value, absoluteExpiration);
            return Task.CompletedTask;
        }
    }
}
