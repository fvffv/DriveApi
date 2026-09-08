using Microsoft.Data.Sqlite;
using SqlSugar;
using System.Data;

namespace drive_api.Services.Db
{
    public class DbEventHandler(ILogger<DbEventHandler> logger)
    {
        private readonly ILogger<DbEventHandler> _logger = logger;

        /// <summary>
        /// SQL 执行后日志记录
        /// </summary>
        /// <param name="sql">生成的SQL语句</param>
        /// <param name="pars">SQL参数</param>
        public void OnLogExecued(string sql, SugarParameter[] pars)
        {
            // 在这里可以注入 ILogger，或者直接用 Console
            // 为了简单演示，我们直接用 Console.WriteLine
            // 生产环境中，你应该使用依赖注入的 ILogger 来记录日志
        }

        /// <summary>
        /// 数据库操作发生错误时触发
        /// </summary>
        /// <param name="ex">捕获到的异常</param>
        public void OnError(SqlSugarException ex)
        {
            // 在这里记录错误日志

            _logger.LogError(ex, ex.Message);
            // 如果需要，可以在这里进行更复杂的处理，
            // 比如记录到文件、发送邮件通知等。
        }
    

    }
}
