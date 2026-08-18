namespace drive_api.Models
{

    public class FileOrDirMoveInfo
    {
        public string[]? FolderIds { get; set; }
        public string[]? FileIds { get; set; }

        public string NewFolderId { get; set; }
    }
}
