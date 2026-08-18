namespace drive_api.Models
{
    public class FileDownLoadInfo
    {
        public Guid FileId { get; set; }
        public Guid UserId { get; set; }
        public string Name { get; set; }
        public ulong SizeInBytes { get; set; }
        public string StoragePath { get; set; }
    }
}
