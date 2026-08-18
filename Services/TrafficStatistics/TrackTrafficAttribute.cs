namespace drive_api.Services.TrafficStatistics
{
    /// <summary>
    /// 标记需要进行上传/下载流量统计的 API 接口
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true, AllowMultiple = false)]
    public class TrackTrafficAttribute : Attribute
    {
    }
}
