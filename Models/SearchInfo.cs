using System.ComponentModel;

namespace drive_api.Models
{
    public class SearchInfo
    {
        [Description("搜索关键词")]
        public string? Keyword { get; set; }
        [Description("文件类型数组,是空则不限制搜索类型,可多选类型: 图片;文档;视频;音频;压缩包;应用;系统;代码;文件夹")]
        public string[]? FileType { get; set; } = { }; 
        [Description("文件最小Bytes,是0则不限制")]
        public ulong? FileSizeInBytesMin { get; set; }
        [Description("文件最大Bytes,是0则不限制")]
        public ulong? FileSizeInBytesMax { get; set; }
        [Description("文件搜索最后修改时间开始部分,格式:yyyy-MM-dd 或 yyyy-MM-dd HH:mm:ss")]
        public string? StarLastModifiedTime { get; set; }
        [Description("文件搜索最后修改时间结束部分 格式:yyyy-MM-dd 或 yyyy-MM-dd HH:mm:ss")]
        public string? EndLastModifiedTime { get; set; }
        [Description("文件搜索创建时间开始部分,格式:yyyy-MM-dd 或 yyyy-MM-dd HH:mm:ss")]
        public string? StarCreationTime { get; set; }
        [Description("文件搜索创建时间结束部分,格式:yyyy-MM-dd 或 yyyy-MM-dd HH:mm:ss")]
        public string? EndCreationTime { get; set; }

        /// <summary>
        /// 时间 类型 大小
        /// </summary>
        [Description("排序方式,是空则默认按时间排序,可多选类型:时间,类型,大小。注: 排列方式不同结果也不同")]
        public string[]? OrderByType { get; set; } = { };

    }
}
