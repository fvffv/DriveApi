using SqlSugar;

namespace drive_api.Models
{
    [SugarTable("file_share", "用户文件分享表")]
    [SugarIndex("ix_file_share_user_deleted_file", nameof(FileShareInfo.UserId), OrderByType.Asc, nameof(FileShareInfo.IsDeleted), OrderByType.Asc, nameof(FileShareInfo.ShareFileId), OrderByType.Asc)]
    [SugarIndex("ix_file_share_file_deleted", nameof(FileShareInfo.ShareFileId), OrderByType.Asc, nameof(FileShareInfo.IsDeleted), OrderByType.Asc)]
    [SugarIndex("ix_file_share_user_deleted_end", nameof(FileShareInfo.UserId), OrderByType.Asc, nameof(FileShareInfo.IsDeleted), OrderByType.Asc, nameof(FileShareInfo.EndValidity), OrderByType.Asc)]
    public class FileShareInfo
    {
        [SugarColumn(IsPrimaryKey = true, ColumnName = "id", ColumnDescription = "文件分享表唯一ID")]
        public Guid Id { get; set; }

        [SugarColumn(ColumnName = "user_id", ColumnDescription = "用户id")]
        public Guid UserId { get; set; }

        [SugarColumn(ColumnName = "file_id", ColumnDescription = "被分享的文件id")]
        public Guid ShareFileId { get; set; }

        [SugarColumn(ColumnName = "begin_validity", ColumnDescription = "有效期开始")]
        public DateTime BeginValidity { get; set; }

        [SugarColumn(ColumnName = "end_validity", ColumnDescription = "有效期结束")]
        public DateTime EndValidity { get; set; }

        [SugarColumn(ColumnName = "password", ColumnDataType = "varchar(10)", IsNullable = true, ColumnDescription = "下载密码")]
        public string Password { get; set; }

        [SugarColumn(ColumnName = "introduction", ColumnDataType = "varchar(150)", IsNullable = true, ColumnDescription = "简介")]
        public string Introduction { get; set; }

        [SugarColumn(ColumnName = "creation_time", ColumnDescription = "创建时间")]
        public DateTime CreationTime { get; set; }

        [SugarColumn(ColumnName = "is_deleted", ColumnDescription = "删除标记")]
        public bool IsDeleted { get; set; }
    }
}
