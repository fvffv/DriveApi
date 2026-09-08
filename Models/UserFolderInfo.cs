using SqlSugar;

namespace drive_api.Models
{
    [SugarTable("folder_info", "用户文件夹表")] // 将类映射到 'folder_info' 表，并添加表注释
    [SugarIndex("ix_folder_user_parent_deleted_created", nameof(UserFolderInfo.UserId), OrderByType.Asc, nameof(UserFolderInfo.ParentId), OrderByType.Asc, nameof(UserFolderInfo.IsDeleted), OrderByType.Asc, nameof(UserFolderInfo.CreationTime), OrderByType.Desc)]
    [SugarIndex("ix_folder_user_id_deleted", nameof(UserFolderInfo.UserId), OrderByType.Asc, nameof(UserFolderInfo.Id), OrderByType.Asc, nameof(UserFolderInfo.IsDeleted), OrderByType.Asc)]
    [SugarIndex("ix_folder_user_parent_name_deleted", nameof(UserFolderInfo.UserId), OrderByType.Asc, nameof(UserFolderInfo.ParentId), OrderByType.Asc, nameof(UserFolderInfo.FolderName), OrderByType.Asc, nameof(UserFolderInfo.IsDeleted), OrderByType.Asc)]
    [SugarIndex("ix_folder_user_name_deleted", nameof(UserFolderInfo.UserId), OrderByType.Asc, nameof(UserFolderInfo.FolderName), OrderByType.Asc, nameof(UserFolderInfo.IsDeleted), OrderByType.Asc)]
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

    /// <summary>
    /// 此模型类专门用于表示文件夹树结构中的节点信息。在sugarSql ToParentListAsync 不支持双主键
    /// </summary>
    [SugarTable("folder_info")]
    public sealed class FolderTreeNode
    {
        [SugarColumn(ColumnName = "id", IsPrimaryKey = true)]
        public Guid Id { get; set; }

        [SugarColumn(ColumnName = "parent_id")]
        public Guid ParentId { get; set; }

        [SugarColumn(ColumnName = "user_id")]
        public Guid UserId { get; set; }

        [SugarColumn(ColumnName = "folder_name")]
        public string FolderName { get; set; } = string.Empty;

        [SugarColumn(ColumnName = "is_deleted")]
        public bool IsDeleted { get; set; }
    }
}
