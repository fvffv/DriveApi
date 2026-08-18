namespace drive_api.Models
{
    public record class ShowUserInfo(Guid UserId, string Username, string Nickname, string AvatarUrl, DateTime CreatedAt, Guid RootFolderId, string Email, UserPreferences Preferences, int Status);


}
