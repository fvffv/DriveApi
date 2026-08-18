namespace drive_api.Models
{
    using System.Collections.Generic;

    /// <summary>
    /// 统计看板数据传输主对象
    /// </summary>
    public class DashboardStatisticsDto
    {
        /// <summary>
        /// 顶部统计摘要卡片数据
        /// </summary>
        public SummaryDataDto SummaryData { get; set; } = new();

        /// <summary>
        /// 文件类型空间分布数据（用于环形图）
        /// </summary>
        public List<FileTypeDataDto> FileTypeData { get; set; } = new();

        /// <summary>
        /// 近期上传趋势活跃度数据（用于平滑折线图）
        /// </summary>
        public List<TrendDataDto> TrendData { get; set; } = new();

        /// <summary>
        /// 大文件空间占用排行数据（用于条形图）
        /// </summary>
        public List<TopFilesDataDto> TopFilesData { get; set; } = new();
    }

    /// <summary>
    /// 顶部统计摘要数据
    /// </summary>
    public class SummaryDataDto
    {
        /// <summary>
        /// 用户总存储容量（纯字节 Bytes）
        /// </summary>
        public long TotalSpaceBytes { get; set; }

        /// <summary>
        /// 用户已使用的存储容量（纯字节 Bytes）
        /// </summary>
        public long UsedSpaceBytes { get; set; }

        /// <summary>
        /// 用户网盘内的总文件数量
        /// </summary>
        public int FileCount { get; set; }

        /// <summary>
        /// 用户当前活跃（未删除/未过期）的分享链接数量
        /// </summary>
        public int ShareCount { get; set; }
    }

    /// <summary>
    /// 单个文件类型的分布数据
    /// </summary>
    public class FileTypeDataDto
    {
        /// <summary>
        /// 文件类型名称（如：视频 (Video)、图片 (Image) 等）
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// 该类型文件占据的总容量大小（纯字节 Bytes）
        /// </summary>
        public long ValueBytes { get; set; }
    }

    /// <summary>
    /// 上传趋势统计数据
    /// </summary>
    public class TrendDataDto
    {
        /// <summary>
        /// 统计周期的时间节点列表（如：周一、周二...），用于图表 X 轴
        /// </summary>
        public string Dates { get; set; }

        /// <summary>
        /// 对应时间节点内的上传文件数量列表，用于图表 Y 轴
        /// </summary>
        public int Uploads { get; set; }
    }

    /// <summary>
    /// 大文件排行统计数据
    /// </summary>
    public class TopFilesDataDto
    {
        /// <summary>
        /// 占用空间最大的文件名称列表，用于图表 Y 轴
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// 对应文件的容量大小列表（纯字节 Bytes），用于图表 X 轴
        /// </summary>
        public long SizeByte { get; set; }
    }
}