namespace drive_api.Models
{
    public class FileOrDirReNameInfo
    {
        public int Type { get; set; } //0文件1文件夹
        public string Id { get; set; }
        public string NewName { get; set; }
    }


}
