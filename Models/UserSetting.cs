namespace drive_api.Models
{
    public class UserSetting
    {
        public int UserNameMaxLength { get; set; } = 50;
        public int UserPassWordMinLength { get; set; } = 6;
        public int UserPassWordMaxLength { get; set; } = 18;
        public string DefaultUserAvatar { get; set; } = "https://example.com/default-avatar.png";
        public int DefaultTotalStorageGb { get; set; } = 100;

    }
}
