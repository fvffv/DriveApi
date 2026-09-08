namespace drive_api.Services.Db
{
    public class DbSetting
    {
        public int DbType { get; set; }
        public string ConnectionString { get; set; }
        public bool LogWriteToDB { get; set; }
        /// <summary>
        /// 如果是true,在第一个注册的用户会被创建为管理员,如果是false,则不会自动创建管理员用户
        /// </summary>
        public bool CreateAdminUser { get; set; }
    }
}
