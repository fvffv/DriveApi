namespace drive_api.Services.Cache
{
    public class CacheSetting
    {
        public string CacheType { get; set; }
        public string ConnectionString { get; set; }
        public int ValidityPeriod { get; set; }
        public int TempDownLoadKeyValidityPeriod { get; set; }
    }
}
