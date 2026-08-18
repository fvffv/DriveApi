using SqlSugar;

namespace drive_api.Models
{
    [SugarTable("file_info", "用户文件表")] // 将类映射到 'file_info' 表，并添加表注释
    [SugarIndex("idx_userid_fileid", nameof(FileInfo.UserId), OrderByType.Desc, nameof(FileInfo.Id), OrderByType.Desc)]
    [SugarIndex("user_id", nameof(FileInfo.UserId), OrderByType.Desc)]
    [SugarIndex("file_id", nameof(FileInfo.Id), OrderByType.Desc, true)]
    [SugarIndex("file_hash", nameof(FileInfo.FileHash), OrderByType.Asc)]
    [SugarIndex("file_type", nameof(FileInfo.FileType), OrderByType.Asc)]
    [SugarIndex("file_size_bytes", nameof(FileInfo.FileSizeInBytes), OrderByType.Asc)]
    [SugarIndex("storage_path", nameof(FileInfo.StoragePath), OrderByType.Asc)]
    [SugarIndex("folder_id", nameof(FileInfo.FolderId), OrderByType.Asc)]
    public class FileInfo
    {
        /// <summary>
        /// 文件唯一ID，主键。
        /// </summary>
        [SugarColumn(IsPrimaryKey = true, ColumnDescription = "主键ID")]
        public Guid Id { get; set; }

        /// <summary>
        /// 所属用户的ID，用于关联用户表。
        /// </summary>
        [SugarColumn(IsPrimaryKey = true, ColumnName = "user_id", ColumnDescription = "所属用户ID")]
        public Guid UserId { get; set; }

        /// <summary>
        /// 文件名称。
        /// </summary>
        [SugarColumn(Length = 255, ColumnName = "file_name", ColumnDescription = "文件名称")]
        public string FileName { get; set; }
        /// <summary>
        /// 文件名称。
        /// </summary>
        [SugarColumn(Length = 255, ColumnName = "file_type", ColumnDescription = "文件类型")]
        public string FileType { get; set; }
        /// <summary>
        /// 文件位置
        /// </summary>
        [SugarColumn(ColumnName = "folder_id", ColumnDescription = "所属文件夹id")]
        public Guid FolderId { get; set; }
        /// <summary>
        /// 文件大小（以字节为单位）。
        /// 文件夹的大小可以为 0。
        /// </summary>
        [SugarColumn(ColumnName = "file_size_bytes", ColumnDescription = "文件大小（字节）")]
        public ulong FileSizeInBytes { get; set; }

        /// <summary>
        /// 文件的存储路径。
        /// 这可以是服务器上的物理路径，也可以是直连地址。
        /// </summary>
        [SugarColumn(Length = 512, ColumnName = "storage_path", IsNullable = true, ColumnDescription = "文件物理存储路径")]
        public string StoragePath { get; set; }

        /// <summary>
        /// 文件的哈希值 (如 SHA256)。
        /// 用于实现“秒传”（快速上传）和文件完整性校验。
        /// 相同哈希值的文件，在物理上可以只存储一份。
        /// 设置索引以加速秒传检查。
        /// </summary>
        [SugarColumn(Length = 64, ColumnName = "hash", IsNullable = true, ColumnDescription = "文件哈希值")]
        public string FileHash { get; set; }
        /// <summary>
        /// 软删除标记。
        /// true: 已删除, false: 正常。
        /// </summary>
        [SugarColumn(ColumnName = "is_deleted", ColumnDescription = "软删除标记")]
        public bool IsDeleted { get; set; }

        /// <summary>
        /// 记录创建时间（文件上传时间）。
        /// </summary>
        [SugarColumn(ColumnName = "creation_time", ColumnDescription = "创建时间")]
        public DateTime CreationTime { get; set; }

        /// <summary>
        /// 记录最后修改时间。
        /// </summary>
        [SugarColumn(ColumnName = "lastmodified_time", ColumnDescription = "最后修改时间")]
        public DateTime LastModifiedTime { get; set; }
    }
}
