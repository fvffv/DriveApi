namespace drive_api.Services.FileManagement
{
    public class FileSetting
    {
        /// <summary>
        /// 最大单个文件大小，单位字节，超过限制的文件将被禁止上传
        /// </summary>
        public ulong MaxFileSize { get; set; }
        /// <summary>
        /// 是否软删除
        /// </summary>
        public bool IsSoftDelete { get; set; }
        /// <summary>
        /// 文件存储的本地路径，必须是绝对路径，且应用程序有读写权限
        /// </summary>
        public string LocalFilePath { get; set; }
        /// <summary>
        /// 文件名长度限制，超过限制的文件名将被禁止上传或创建
        /// </summary>
        public int FileOrDirNameLengthLimit { get; set; }
        /// <summary>
        /// 黑名单关键词
        /// </summary>
        public string FileOrDirNameBlacklist { get; set; }
        /// <summary>
        /// 黑名单正则表达式，匹配的文件或目录名将被禁止上传或创建
        /// </summary>
        public string FileOrDirNameBlacklistRegExp { get; set; }
        /// <summary>
        /// 临时文件路径
        /// </summary>
        public string TempFilePath { get; set; }
        /// <summary>
        /// 文件分块上传的大小，默认10MB
        /// </summary>
        public ulong FileChunkSizeBytes { get; set; } = 10485760;
        /// <summary>
        /// 分片任务超时多少秒后被清理，默认 3600 秒（1 小时）延迟执行 取决于CleanupChunkFilesWorker循环检测时间
        /// </summary>
        public int CleanupChunkFilesWorkerTimeOutSecond { get; set; } = 3600;
        /// <summary>
        /// 分片任务清理循环检测时间，默认 600 秒（10 分钟）执行检测一次
        /// </summary>
        public int CleanupChunkFilesWorkerLoopTimeSecond { get; set; } = 600;
    }
}
