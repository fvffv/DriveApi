namespace drive_api.Services.AI
{
    public class AISetting
    {
        public bool Enable { get; set; }

        /// <summary>
        /// 图形相关度阈值，范围0-1，越接近1表示越相似
        /// </summary>
        public double ImageCosineThreshold { get; set; } = 0.25;
        /// <summary>
        /// 文本相关度阈值，范围0-1，越接近1表示越相似
        /// </summary>
        public double TextCosineThreshold { get; set; } = 0.40;
        /// <summary>
        /// 落差比   最大落差 / 其他落差的中位数    判断相关结果和弱相关结果之间的断层
        ///
        /// 文本语义搜索文档原理
        /// 最低阈值TextCosineThreshold过滤明显无关结果
        /// 找最大落差，这里通常能过滤掉大部分弱相关结果，判断这个落差是否足够异常
        /// 之后引入 TextGapRatio   用来解决 当都是强相关结果 也会有落差的情况，只有落差比异常大于这个值才截断，否则全部保留，以免丢掉一部分强相关结果
        /// </summary>
        public double TextGapRatio { get; set; } = 2.7;

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
