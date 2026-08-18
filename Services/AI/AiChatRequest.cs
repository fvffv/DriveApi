namespace drive_api.Services.AI
{
    public class AiChatRequest
    {
        public string UserInput { get; set; }

        public List<ChatMessage> History { get; set; }

        public ClientContext Context { get; set; }
    }
    public class ChatMessage
    {
        public string Role { get; set; }

        public string Content { get; set; }
    }

    public class ClientContext
    {
        public string CurrentFolderId { get; set; }

        public string CurrentPath { get; set; }

        public string CurrentDomain { get; set; }

        public string Client { get; set; }

        public string BackEnd { get; set; }
    }
}
