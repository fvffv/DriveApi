namespace drive_api.Models
{
    public class Other
    {
    }

    public record class LoginInfo(string usernameOrEmail, string password);
    public record class LoginResult(string token, UserPreferences userPreferences);
}
