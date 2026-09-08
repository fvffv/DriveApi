using SqlSugar;

namespace drive_api.Models
{

    [SugarTable("text_vectorcs", "文本向量，用于文本类语义搜索")]
    [SugarIndex("ix_text_vectorcs_user_file_chunk", nameof(TextVectorcs.UserId), OrderByType.Asc, nameof(TextVectorcs.FileId), OrderByType.Asc, nameof(TextVectorcs.ChunkId), OrderByType.Asc)]
    public class TextVectorcs
    {
        [SugarColumn(ColumnName = "file_id",  ColumnDescription = "图片的id")]
        public Guid FileId { get; set; }

        [SugarColumn(ColumnName = "user_id", ColumnDescription = "用户id")]
        public Guid UserId { get; set; }

        [SugarColumn(ColumnName = "chunk_id", ColumnDescription = "文档分块向量序号id")]
        public int ChunkId { get; set; }
        [SugarColumn(ColumnName = "vector", IsArray = true, ColumnDataType = "vector(1024)", ColumnDescription = "图片的向量")]
        public float[] Vector { get; set; }
    }


   

}
