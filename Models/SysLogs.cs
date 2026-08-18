using SqlSugar;

namespace drive_api.Models
{
    [SugarTable("sys_logs", "系统日志表")]
    public class SysLogs
    {


        /// <summary>
        /// 格式化后的日志消息
        /// </summary>
        [SugarColumn(ColumnName = "message", ColumnDataType = "text", IsNullable = true, ColumnDescription = "日志消息")]
        public string? Message { get; set; }

        /// <summary>
        /// 日志消息模板（带有占位符的原始消息）
        /// </summary>


        /// <summary>
        /// 日志级别 (通常映射为枚举，如 0:Verbose, 1:Debug, 2:Info, 3:Warning, 4:Error, 5:Fatal)
        /// </summary>
        [SugarColumn(ColumnName = "level", IsNullable = true, ColumnDescription = "日志级别")]
        public int? Level { get; set; }

        /// <summary>
        /// 记录时间 (带 6 位微秒精度的 timestamp)
        /// </summary>
        [SugarColumn(ColumnName = "timestamp", IsNullable = true, ColumnDescription = "记录时间")]
        public DateTime? Timestamp { get; set; }

        /// <summary>
        /// 异常详细信息（堆栈跟踪等）
        /// </summary>
        [SugarColumn(ColumnName = "exception", ColumnDataType = "text", IsNullable = true, ColumnDescription = "异常信息")]
        public string? Exception { get; set; }

        /// <summary>
        /// 结构化日志事件属性 (PostgreSQL JSONB 类型)
        /// </summary>
        [SugarColumn(ColumnName = "log_event", ColumnDataType = "jsonb", IsNullable = true, ColumnDescription = "日志事件JSON")]
        public string? LogEvent { get; set; }


    }
}
