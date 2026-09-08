using SqlSugar;

namespace drive_api.Models
{
    [SugarTable("sys_logs")]
    public class SqliteSysLogs
    {
        [SugarColumn(
            ColumnName = "id",
            IsPrimaryKey = true,
            IsIdentity = true)]
        public long Id { get; set; }

        [SugarColumn(ColumnName = "RenderedMessage")]
        public string? Message { get; set; }

        [SugarColumn(ColumnName = "Level")]
        public string? Level { get; set; }

        [SugarColumn(ColumnName = "Timestamp")]
        public DateTime? Timestamp { get; set; }

        [SugarColumn(ColumnName = "Exception")]
        public string? Exception { get; set; }

        [SugarColumn(ColumnName = "Properties")]
        public string? LogEvent { get; set; }
    }
}
