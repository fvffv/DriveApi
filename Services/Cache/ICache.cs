namespace drive_api.Services.Cache
{
    /// <summary>
    /// 通用缓存服务接口
    /// </summary>
    public interface ICache
    {
        /// <summary>
        /// 尝试从缓存中获取一个值。
        /// </summary>
        /// <typeparam name="T">值的类型</typeparam>
        /// <param name="key">缓存键</param>
        /// <returns>如果找到，则返回缓存的值；否则返回 default(T)。</returns>
        Task<T> GetAsync<T>(string cacheName, string key);

        /// <summary>
        /// 设置一个缓存项，可以指定绝对过期时间或滑动过期时间。
        /// </summary>
        /// <typeparam name="T">值的类型</typeparam>
        /// <param name="key">缓存键</param>
        /// <param name="value">要缓存的值</param>
        /// <param name="absoluteExpirationRelativeToNow">相对于现在的绝对过期时间。</param>
        /// <param name="slidingExpiration">滑动过期时间。</param>
        Task SetAsync<T>(string cacheName, string key, T value, DateTimeOffset absoluteExpiration);

        /// <summary>
        /// 从缓存中移除一个项。
        /// </summary>
        /// <param name="key">缓存键</param>
        Task RemoveAsync(string cacheName, string key);

        /// <summary>
        /// 检查缓存中是否存在指定的键。
        /// </summary>
        /// <param name="key">缓存键</param>
        /// <returns>如果存在则返回 true，否则返回 false。</returns>
        Task<bool> ExistsAsync(string cacheName, string key);

    }
}
