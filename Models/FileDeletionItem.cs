namespace drive_api.Models
{
    public class FileDeletionItem
    {
        public Guid FileId { get; set; }
        public string StoragePath { get; set; }
        public string? FileHash { get; set; }
    }
}
