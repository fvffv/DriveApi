namespace drive_api.Models
{
    public class StorageCapacityInfo
    {
        /// <summary>
        /// 使用中容量
        /// </summary>
        public ulong UsedSpaceInBytes { get; set; }
        /// <summary>
        /// 总共容量
        /// </summary>
        public ulong TotalSpaceInBytes { get; set; }
        /// <summary>
        /// 剩余容量
        /// </summary>
        public ulong FreeSpaceInBytes
        {
            get
            {
                if (TotalSpaceInBytes < UsedSpaceInBytes)
                {
                    return 0;
                }
                return TotalSpaceInBytes - UsedSpaceInBytes;
            }
        }
    }
}
