namespace drive_api.Services.Db
{
    public class DbSetting
    {
        public int DbType { get; set; }
        public string ConnectionString { get; set; }
        public bool LogWriteToDB { get; set; }
    }
}
