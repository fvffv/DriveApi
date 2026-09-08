namespace drive_api.Models
{
    public class DefaultMsg
    {
        public int Status { get; set; }
        public string Msg { get; set; }
        public Object Data { get; set; }

        public DefaultMsg(int status, string msg, Object data)
        {
            Status = status;
            Msg = msg;
            Data = data;
        }
    }


    public class DefaultMsg<T>
    {
        public int Status { get; set; }
        public string Msg { get; set; }
        public T Data { get; set; }

        public DefaultMsg(int status, string msg, T data)
        {
            Status = status;
            Msg = msg;
            Data = data;
        }
    }
}
