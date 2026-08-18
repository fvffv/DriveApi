using System.Text.Json.Serialization;

namespace drive_api.Models
{
    public class MultipartUploadModels
    {
    }

    /// <summary>
    /// 初始化分片上传请求的模型。
    /// </summary>
    /// <param name="FileName"></param>
    /// <param name="FileSizeInBytes"></param>
    /// <param name="FileHash"></param>
    public record MultipartUploadInitRequest(string FileName,ulong FileSizeInBytes,string FileHash);

    /// <summary>
    /// 分段上传任务核心信息模型
    /// </summary>
    public class UploadTaskModel
    {
        /// <summary>
        /// 任务唯一标识
        /// </summary>
        [JsonPropertyName("upload_id")]
        public Guid UploadId { get; set; } 

        /// <summary>
        /// 所属用户ID
        /// </summary>
        [JsonPropertyName("user_id")]
        public Guid UserId { get; set; } 

        /// <summary>
        /// 当前任务状态 UPLOADING上传中 END结束
        /// </summary>
        [JsonPropertyName("status")]
        public string Status { get; set; } = "UPLOADING";

        /// <summary>
        /// 文件的静态元数据
        /// </summary>
        [JsonPropertyName("meta")]
        public UploadMetaModel Meta { get; set; } = new UploadMetaModel();

        /// <summary>
        /// 已完成上传的分片进度列表
        /// </summary>
        [JsonPropertyName("uploaded_chunks")]
        public List<UploadedChunkModel> UploadedChunks { get; set; } = new List<UploadedChunkModel>();

        /// <summary>
        /// 任务创建时间戳
        /// </summary>
        [JsonPropertyName("created_at")]
        public long CreatedAt { get; set; } = DateTimeOffset.Now.ToUnixTimeSeconds();

    }

    /// <summary>
    /// 上传文件的静态元数据模型
    /// </summary>
    public class UploadMetaModel
    {
        /// <summary>
        /// 原始文件名
        /// </summary>
        [JsonPropertyName("file_name")]
        public string FileName { get; set; } = string.Empty;

        /// <summary>
        /// 文件完整哈希值256
        /// </summary>
        [JsonPropertyName("file_hash")]
        public string FileHash { get; set; } = string.Empty;

        /// <summary>
        /// 文件总大小 字节
        /// </summary>
        [JsonPropertyName("total_size")]
        public ulong TotalSize { get; set; }

        /// <summary>
        /// 总分片数量
        /// </summary>
        [JsonPropertyName("total_chunks")]
        public int TotalChunks { get; set; }
    }

    /// <summary>
    /// 单个已上传成功的分片信息模型
    /// </summary>
    public class UploadedChunkModel
    {
        /// <summary>
        /// 分片序号 从 0 开始
        /// </summary>
        [JsonPropertyName("index")]
        public int Index { get; set; }

        /// <summary>
        /// 当前分片实际大小 字节
        /// </summary>
        [JsonPropertyName("size")]
        public int Size { get; set; }

        /// <summary>
        /// 该分片完成上传的时间戳
        /// </summary>  
        [JsonPropertyName("completed_at")]
        public long CompletedAt { get; set; }
    }
}
