using SqlSugar;

namespace drive_api.Services.ExternalDrive
{
    [SugarTable("external_drives", "外部网盘账户表")]
    public class ExternalDriveModel
    {
        /// <summary>
        /// 外部网盘唯一ID
        /// </summary>

        [SugarColumn(IsPrimaryKey = true, ColumnName = "external_id", ColumnDescription = "外部网盘唯一ID")]
        public Guid ExternalId { get; set; }
        /// <summary>
        /// 所属用户的ID，用于关联用户表。
        /// </summary>

        [SugarColumn(ColumnName = "user_id", ColumnDescription = "用户id")]
        public Guid UserId { get; set; }
        /// <summary>
        ///  显示名称
        /// </summary>
        [SugarColumn(ColumnName = "display_name",Length = 30,IsNullable = true, ColumnDescription = "显示名称")]
        public string? DisplayName { get; set; }
        /// <summary>
        /// 外部网盘类型，
        /// </summary>
        [SugarColumn(ColumnName = "drive_type", ColumnDescription = "外部网盘类型")]
        public CloudDriveType DriveType { get; set; }
        /// <summary>
        /// 网盘应用配置 JSON，仅保存 AppId、AppKey、SecretKey 等应用级参数。
        /// 用户 AccessToken、RefreshToken 和设备码不写入该字段。
        /// </summary>
        [SugarColumn(ColumnName = "credential_data",ColumnDataType = "text",IsNullable = true)]
        public string? CredentialData { get; set; }

        [SugarColumn(ColumnName = "creation_time", ColumnDescription = "创建时间")]
        public DateTime CreationTime { get; set; }

        [SugarColumn(ColumnName = "is_deleted", ColumnDescription = "删除标记")]
        public bool IsDeleted { get; set; }
    }

    /// <summary>
    /// 网盘提供者类型。
    /// 数据库存储对应的整数值。
    /// </summary>
    public enum CloudDriveType
    {
  

        /// <summary>
        /// 百度网盘。
        /// </summary>
        Baidu = 1,

        /// <summary>
        /// 阿里云盘。
        /// </summary>
        Aliyun = 2,

        /// <summary>
        /// OneDrive。
        /// </summary>
        OneDrive = 3,


    }

    /// <summary>
    /// 获取网盘 Token 时执行的授权动作。
    /// </summary>
    public enum DriveTokenAction
    {
        /// <summary>
        /// 获取前端展示所需的登录二维码、用户码或登录网址。
        /// </summary>
        GetAuthorizationInfo = 1,

        /// <summary>
        /// 校验用户是否已完成登录并在服务端保存 Token。
        /// </summary>
        CheckAuthorization = 2
    }

    public class DriveParams
    {
        /// <summary>
        /// 字段名
        /// </summary>
        public string Target { get; set; }
        /// <summary>
        /// 标签
        /// </summary>
        public string Label { get; set; }
        /// <summary>
        /// 介绍
        /// </summary>
        public string Description { get; set; }
    }

}
