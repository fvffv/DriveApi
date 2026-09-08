using System.Text.Json.Serialization;
using drive_api.Services.ExternalDrive;

namespace drive_api.Models;

/// <summary>外部网盘目录响应。不同网盘统一映射为同一目录模型。</summary>
public sealed class ExternalDriveDirectoryResult
{
    [JsonPropertyName("path")] public string Path { get; set; } = "/";
    [JsonPropertyName("items")] public ExternalDriveItem[] Items { get; set; } = [];
    [JsonPropertyName("has_more")] public bool HasMore { get; set; }
    [JsonPropertyName("start")] public int Start { get; set; }
    [JsonPropertyName("limit")] public int Limit { get; set; }
}

/// <summary>外部网盘文件/文件夹通用条目。</summary>
public class ExternalDriveItem
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("path")] public string Path { get; set; } = string.Empty;
    [JsonPropertyName("is_directory")] public bool IsDirectory { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("created_at")] public DateTimeOffset? CreatedAt { get; set; }
    [JsonPropertyName("modified_at")] public DateTimeOffset? ModifiedAt { get; set; }
    [JsonPropertyName("hash")] public string? Hash { get; set; }
}

public sealed class ExternalDriveFileInfoResult : ExternalDriveItem
{
    [JsonPropertyName("download_url")] public string? DownloadUrl { get; set; }
    [JsonPropertyName("mime_type")] public string? MimeType { get; set; }
}

public sealed class ExternalDriveDownloadResult
{
    [JsonPropertyName("file_id")] public string FileId { get; set; } = string.Empty;
    [JsonPropertyName("file_name")] public string FileName { get; set; } = string.Empty;
    [JsonPropertyName("download_url")] public string DownloadUrl { get; set; } = string.Empty;
    [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    [JsonPropertyName("supports_range")] public bool SupportsRange { get; set; } = true;
}

public sealed class ExternalDriveUploadResult
{
    [JsonPropertyName("file")] public ExternalDriveFileInfoResult? File { get; set; }
    [JsonPropertyName("upload_id")] public string? UploadId { get; set; }
    [JsonPropertyName("status")] public string Status { get; set; } = "completed";
}

public sealed class ExternalDriveUploadSessionResult
{
    [JsonPropertyName("upload_id")] public string UploadId { get; set; } = string.Empty;
    [JsonPropertyName("file_name")] public string FileName { get; set; } = string.Empty;
    [JsonPropertyName("file_size")] public long FileSize { get; set; }
    [JsonPropertyName("chunk_size")] public int ChunkSize { get; set; }
    [JsonPropertyName("status")] public string Status { get; set; } = "UPLOADING";
    [JsonPropertyName("uploaded_chunks")] public ExternalDriveChunkResult[] UploadedChunks { get; set; } = [];
}

public sealed class ExternalDriveChunkResult
{
    [JsonPropertyName("upload_id")] public string? UploadId { get; set; }
    [JsonPropertyName("chunk_index")] public int ChunkIndex { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("md5")] public string? Md5 { get; set; }
}

public sealed class ExternalDriveOperationResult
{
    [JsonPropertyName("operation")] public string Operation { get; set; } = string.Empty;
    [JsonPropertyName("file_id")] public string? FileId { get; set; }
    [JsonPropertyName("path")] public string? Path { get; set; }
    [JsonPropertyName("task_id")] public long? TaskId { get; set; }
}

/// <summary>外部网盘批量移动请求。</summary>
public sealed class ExternalDriveMoveRequest
{
    public CloudDriveType DriveType { get; set; }
    public string[] FileIdOrPaths { get; set; } = [];
    public string TargetFolderIdOrPath { get; set; } = string.Empty;
    public string AccessToken { get; set; } = string.Empty;
}

public sealed class ExternalDriveShareResult
{
    [JsonPropertyName("share_id")] public string? ShareId { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("short_url")] public string? ShortUrl { get; set; }
    [JsonPropertyName("password")] public string? Password { get; set; }
    [JsonPropertyName("expires_at")] public DateTimeOffset? ExpiresAt { get; set; }
}

public sealed class ExternalDriveSearchResult
{
    [JsonPropertyName("items")] public ExternalDriveItem[] Items { get; set; } = [];
    [JsonPropertyName("has_more")] public bool HasMore { get; set; }
    [JsonPropertyName("page")] public int Page { get; set; }
    [JsonPropertyName("limit")] public int Limit { get; set; }
}

public sealed class ExternalDriveAuthorizationResult
{
    [JsonPropertyName("action")] public DriveTokenAction Action { get; set; }
    /// <summary>设备码轮询会话标识，仅用于后续 action=2 请求，不会写入数据库。</summary>
    [JsonPropertyName("authorization_session_id")] public string? AuthorizationSessionId { get; set; }
    [JsonPropertyName("user_code")] public string? UserCode { get; set; }
    [JsonPropertyName("verification_url")] public string? VerificationUrl { get; set; }
    [JsonPropertyName("verification_url_complete")] public string? VerificationUrlComplete { get; set; }
    [JsonPropertyName("qrcode_url")] public string? QrCodeUrl { get; set; }
    [JsonPropertyName("expires_in")] public int? ExpiresIn { get; set; }
    [JsonPropertyName("interval")] public int? Interval { get; set; }
    [JsonPropertyName("external_id")] public Guid? ExternalId { get; set; }
    [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
    [JsonPropertyName("authorized")] public bool Authorized { get; set; }
}

/// <summary>已绑定外部网盘账户的公开信息，不包含 CredentialData。</summary>
public sealed class ExternalDriveAccountResult
{
    [JsonPropertyName("external_id")] public Guid ExternalId { get; set; }
    [JsonPropertyName("display_name")] public string DisplayName { get; set; } = string.Empty;
    [JsonPropertyName("drive_type")] public CloudDriveType DriveType { get; set; }
    [JsonPropertyName("created_at")] public DateTime CreationTime { get; set; }
}
