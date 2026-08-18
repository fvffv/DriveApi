namespace driveApi.Services.JWT
{
    public class JwtSetting
    {
        public string SigningKey { get; set; } = string.Empty;
        public int ExpireSeconds { get; set; }

        public string Issuer { get; set; } = string.Empty;
        public string Audience { get; set; } = string.Empty;

    }
}
