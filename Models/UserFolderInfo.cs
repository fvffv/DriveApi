using SqlSugar;

namespace drive_api.Models
{
    [SugarTable("folder_info", "用户文件夹表")] // 将类映射到 'folder_info' 表，并添加表注释
    [SugarIndex("idx_userid_fileid", nameof(UserFolderInfo.UserId), OrderByType.Desc, nameof(UserFolderInfo.Id), OrderByType.Desc)]
    [SugarIndex("id", nameof(UserFolderInfo.Id), OrderByType.Asc)]
    [SugarIndex("parent_id", nameof(UserFolderInfo.ParentId), OrderByType.Asc)]
    [SugarIndex("user_id", nameof(UserFolderInfo.UserId), OrderByType.Asc)]
    public class UserFolderInfo
    {
        /// <summary>
        /// 文件夹唯一ID，主键。
        /// </summary>
        [SugarColumn(IsPrimaryKey = true, ColumnDescription = "主键ID")]
        public Guid Id { get; set; }
        [SugarColumn(ColumnDescription = "父文件夹ID", ColumnName = "parent_id")]
        public Guid ParentId { get; set; }

        /// <summary>
        /// 所属用户的ID，用于关联用户表。
        /// </summary>
        [SugarColumn(IsPrimaryKey = true, ColumnName = "user_id", ColumnDescription = "所属用户ID")]
        public Guid UserId { get; set; }

        /// <summary>
        /// 文件夹名称。
        /// </summary>
        [SugarColumn(Length = 255, ColumnName = "folder_name", ColumnDescription = "文件夹名称")]
        public string FolderName { get; set; }
        /// <summary>
        /// 文件夹位置
        /// </summary>
        //[SugarColumn(ColumnDescription = "文件夹位置")]
        //public string Path { get; set; }

        /// <summary>
        /// 记录创建时间（文件上传时间）。
        /// </summary>
        [SugarColumn(ColumnName = "creation_time", ColumnDescription = "创建时间")]
        public DateTime CreationTime { get; set; }
        /// <summary>
        /// 删除标记。
        /// true: 已删除, false: 正常。
        /// </summary>
        [SugarColumn(ColumnName = "is_deleted", ColumnDescription = "删除标记")]
        public bool IsDeleted { get; set; }
    }
}
