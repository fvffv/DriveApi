namespace drive_api.Services.MQ
{
    public class MQSetting
    {
        public string MqType { get; set; } = "MemoryMQ";
        public string ConnectionString { get; set; } = "localhost:5672";

    }
}
