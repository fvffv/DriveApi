

using SqlSugar;

namespace drive_api.Models
{
    /// <summary>
    /// 用户信息实体类，适配 SqlSugar ORM 框架。
    /// </summary>
    [SugarTable("users", "用户信息表")] // 将类映射到 'users' 表，并添加表注释
    [SugarIndex("ux_users_user_id", nameof(User.UserId), OrderByType.Asc, true)]
    [SugarIndex("ux_users_username", nameof(User.Username), OrderByType.Asc, true)]
    [SugarIndex("ux_users_email", nameof(User.Email), OrderByType.Asc, true)]
    public class User
    {
        /// <summary>
        /// 用户的唯一ID，主键
        /// </summary>

        [SugarColumn(IsPrimaryKey = true, ColumnName = "user_id", ColumnDescription = "用户唯一ID")]

        public Guid UserId { get; set; }

        /// <summary>
        /// 登录用户名，必须唯一。
        /// </summary>
        [SugarColumn(IsPrimaryKey = true, ColumnName = "username", ColumnDataType = $"varchar(50)", IsNullable = false, ColumnDescription = "用户名")]
        public string Username { get; set; } = string.Empty;

        /// <summary>
        /// 用户的电子邮箱，用于登录和通知，必须唯一。
        /// </summary>
        [SugarColumn(IsPrimaryKey = true, ColumnName = "email", ColumnDataType = "varchar(255)", IsNullable = false, ColumnDescription = "登录邮箱，唯一")]
        public string Email { get; set; } = string.Empty;

        /// <summary>
        /// 加密后的密码哈希值。
        /// </summary>
        [SugarColumn(ColumnName = "password_hash", ColumnDataType = "varchar(255)", IsNullable = false, ColumnDescription = "加密后的密码哈希")]
        public string PasswordHash { get; set; } = string.Empty;

        /// <summary>
        /// 用户的昵称，用于界面显示。可以为空。
        /// </summary>
        [SugarColumn(ColumnName = "nickname", ColumnDataType = "varchar(50)", IsNullable = true, ColumnDescription = "用户昵称，用于显示")]
        public string? Nickname { get; set; }

        /// <summary>
        /// 指向用户头像图片的URL。可以为空。
        /// </summary>
        [SugarColumn(ColumnName = "avatar_url", ColumnDataType = "varchar(512)", IsNullable = true, ColumnDescription = "头像图片的URL")]
        public string? AvatarUrl { get; set; }

        /// <summary>
        /// 分配给该用户的总存储空间，单位为 GB。
        /// </summary>
        [SugarColumn(ColumnName = "total_storage_gb", ColumnDataType = "decimal(10, 2)", IsNullable = false, DefaultValue = "0.00", ColumnDescription = "总存储空间额度(GB)")]
        public decimal TotalStorageGB { get; set; }

        ///// <summary>
        ///// 用户已使用的存储空间，单位为字节 (Bytes)。
        ///// </summary>
        //[SugarColumn(ColumnName = "used_storage_bytes", IsNullable = false, DefaultValue = "0", ColumnDescription = "已用存储空间(Bytes)")]
        //public ulong UsedStorageBytes { get; set; }

        /// <summary>
        /// 用户的账户状态。
        /// </summary>
        [SugarColumn(ColumnName = "status", IsNullable = false, DefaultValue = "0", ColumnDescription = "账户状态：0正常 1不正常")]
        public int Status { get; set; }


        /// <summary>
        /// 账户的创建时间。
        /// </summary>
        [SugarColumn(ColumnName = "created_at", IsNullable = false, IsOnlyIgnoreUpdate = true, ColumnDescription = "账户创建时间")] // IsOnlyIgnoreUpdate = true 表示更新时忽略此列
        public DateTime CreatedAt { get; set; }


        /// <summary>
        /// 用户最后一次成功登录的时间。
        /// </summary>
        [SugarColumn(ColumnName = "last_login_at", IsNullable = true, ColumnDescription = "最后一次登录时间")]
        public DateTime? LastLoginAt { get; set; }

        /// <summary>
        /// 根目录id。
        /// </summary>
        [SugarColumn(ColumnName = "root_folder_id", IsNullable = true, ColumnDescription = "根目录id")]
        public Guid RootFolderId { get; set; }
        [SugarColumn(IsJson = true, IsNullable = true)]
        public UserPreferences Preferences { get; set; }

    }
}
