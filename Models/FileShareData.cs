namespace drive_api.Models
{
    public class FileShareData
    {
        public Guid ShareFileId { get; set; }

        public DateTime BeginValidity { get; set; }

        public DateTime EndValidity { get; set; }

        public string? Password { get; set; }

        public string? Introduction { get; set; }

        public FileShareData()
        {
        }

        public FileShareData(Guid shareFileId, DateTime beginValidity, DateTime endValidity, string? password, string? introduction)
        {
            ShareFileId = shareFileId;
            BeginValidity = beginValidity;
            EndValidity = endValidity;
            Password = password;
            Introduction = introduction;
        }
    }

}
