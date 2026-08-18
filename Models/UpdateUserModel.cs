namespace drive_api.Models
{
    public class UpdateUserModel
    {
        public Guid UserId { get; set; }
        /// <summary>
        /// 用户名
        /// </summary>
        public string UserName { get; set; }
        /// <summary>
        /// 邮箱
        /// </summary>
        public string Email { get; set; }
        /// <summary>
        /// 密码hash265
        /// </summary>
        public string? Password { get; set; }
        /// <summary>
        /// 状态
        /// </summary>
        public int Status { get; set; }
        /// <summary>
        /// 昵称
        /// </summary>
        public string NickName { get; set; }
        /// <summary>
        /// 总存储空间
        /// </summary>
        public decimal TotalStorageGB { get; set; }

        /// <summary>
        /// 偏好
        /// </summary>
        public UserPreferences Preferences { get; set; }
    }

    public class RegUserModel
    {
        /// <summary>
        /// 用户名
        /// </summary>
        public string UserName { get; set; }
        /// <summary>
        /// 邮箱
        /// </summary>
        public string Email { get; set; }
        /// <summary>
        /// 密码hash265
        /// </summary>
        public string? Password { get; set; }
        /// <summary>
        /// 昵称
        /// </summary>
        public string NickName { get; set; }
        /// <summary>
        /// 总存储空间
        /// </summary>
        public decimal TotalStorageGB { get; set; }
        /// <summary>
        /// 状态
        /// </summary>
        public int Status { get; set; }
        /// <summary>
        /// 偏好
        /// </summary>
    }
}
