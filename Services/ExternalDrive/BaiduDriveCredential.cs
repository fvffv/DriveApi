using System.Text.Json;

namespace drive_api.Services.ExternalDrive;

/// <summary>
/// 百度开放平台应用凭据。数据库中只保存这三个应用级字段，
/// access_token、refresh_token 和设备码均不属于 CredentialData。
/// </summary>
public sealed record BaiduDriveCredential(string AppId, string AppKey, string SecretKey)
{
    public static BaiduDriveCredential Parse(string? credentialData)
    {
        if (string.IsNullOrWhiteSpace(credentialData))
        {
            throw new ArgumentException("百度网盘 CredentialData 不能为空");
        }

        using JsonDocument document = JsonDocument.Parse(credentialData);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("百度网盘 CredentialData 必须是 JSON 对象");
        }

        JsonElement root = document.RootElement;
        string appId = GetRequiredString(root, "AppId", "app_id", "appid");
        string appKey = GetRequiredString(root, "AppKey", "app_key", "appkey", "client_id");
        string secretKey = GetRequiredString(root, "SecretKey", "secret_key", "secretkey", "client_secret");
        return new BaiduDriveCredential(appId, appKey, secretKey);
    }

    /// <summary>
    /// 规范化后再入库，明确丢弃 Token、RefreshToken、redirect_uri 等非应用配置字段。
    /// </summary>
    public static string Normalize(string credentialData)
    {
        BaiduDriveCredential credential = Parse(credentialData);
        return JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["app_id"] = credential.AppId,
            ["app_key"] = credential.AppKey,
            ["secret_key"] = credential.SecretKey
        });
    }

    private static string GetRequiredString(JsonElement root, params string[] names)
    {
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (!names.Any(name => string.Equals(name, property.Name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            string? value = property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString()
                : property.Value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False
                    ? property.Value.ToString()
                    : null;
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        throw new ArgumentException($"百度网盘 CredentialData 缺少 {names[0]}");
    }
}
