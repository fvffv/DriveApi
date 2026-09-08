using SqlSugar;

namespace drive_api.Models
{
    [SugarTable("traffic_statistics", "每日流量记录")]
    [SugarIndex("ux_traffic_statistics_date", nameof(TrafficStatisticsModel.Date), OrderByType.Desc, true)]
    public class TrafficStatisticsModel
    {
        [SugarColumn(IsPrimaryKey = true, ColumnName = "id", ColumnDescription = "唯一id")]
        public Guid Id { get; set; }
        /// <summary>
        /// 日期
        /// </summary>
        [SugarColumn(IsPrimaryKey = true, ColumnName = "date", ColumnDescription = "唯一日期")]
        public DateTime Date { get; set; }
        /// <summary>
        /// 上传流量
        /// </summary>
        [SugarColumn(ColumnName = "upload_bytes", ColumnDescription = "上传流量")]
        public long UploadBytes { get; set; }
        /// <summary>
        /// 下载流量
        /// </summary>
        [SugarColumn(ColumnName = "download_bytes", ColumnDescription = "下载流量")]
        public long DownloadBytes { get; set; }
    }
}
