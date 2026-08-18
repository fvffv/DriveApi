namespace drive_api.Models
{
    public class UserFilesInfo
    {
        public UserFilesInfoItem[] FileInfos { get; set; }
        public UserDirsInfoItem[] Dirs { get; set; }

        public int TotalFileCount { get; set; }
    }

    public class UserFilesInfoItem
    {
        public Guid Id { get; set; }
        public string FileName { get; set; }
        public ulong FileSizeInBytes { get; set; }
        public string FileHash { get; set; }
        public Guid FolderId { get; set; }
        public DateTime CreationTime { get; set; }
        public DateTime LastModifiedTime { get; set; }
        public string FileShare { get; set; }


    }
    public class UserDirsInfoItem
    {
        public Guid Id { get; set; }
        public string FolderName { get; set; }
        public DateTime CreationTime { get; set; }
    }

}
