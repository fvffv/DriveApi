namespace drive_api.Services.Config
{
    public class ServerSettings
    {
        public EndpointConfig IPv4 { get; set; }
        public EndpointConfig IPv6 { get; set; }
    }

    public class EndpointConfig
    {
        public string Ip { get; set; } = string.Empty;
        public int Port { get; set; }
        public bool EnableSsl { get; set; }
        public string CertPath { get; set; } = string.Empty;
        public string CertPassword { get; set; } = string.Empty;
    }
}

