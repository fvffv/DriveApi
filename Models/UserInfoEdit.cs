namespace drive_api.Models
{
    public class UserInfoEdit
    {
        public string UserNick { get; set; }

    }
    public class UserPasswordEdit
    {
        public string OldPassword { get; set; }
        public string NewPassword { get; set; }
    }
}
