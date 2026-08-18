using SqlSugar;

namespace drive_api.Models
{
    [SugarTable("img_vectorcs", "图片向量，用于语义搜索")]
    [SugarIndex("idx_userid_fileid", nameof(ImgVectorcs.UserId), OrderByType.Desc, nameof(ImgVectorcs.FileId), OrderByType.Desc)]
    public class ImgVectorcs
    {
        [SugarColumn(ColumnName = "file_id", IsPrimaryKey = true, ColumnDescription = "图片的id")]
        public Guid FileId { get; set; }
        [SugarColumn(ColumnName = "user_id", ColumnDescription = "用户id")]
        public Guid UserId { get; set; }
        [SugarColumn(ColumnName = "vector", IsArray = true, ColumnDataType = "vector(768)", ColumnDescription = "图片的向量")]
        public float[] Vector { get; set; }
    }
}
