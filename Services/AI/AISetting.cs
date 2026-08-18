namespace drive_api.Services.AI
{
    public class AISetting
    {
        public bool Enable { get; set; }

        public double ImageCosineThreshold { get; set; }

        public string BaseURL { get; set; }

        public string ApiKey { get; set; }

        public string Model { get; set; }

        public bool IsIncomplete
        {
            get
            {
                if (!string.IsNullOrEmpty(BaseURL) && !string.IsNullOrEmpty(Model))
                {
                    return string.IsNullOrEmpty(ApiKey);
                }
                return true;
            }
        }
    }

}
