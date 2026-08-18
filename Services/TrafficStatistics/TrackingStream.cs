namespace drive_api.Services.TrafficStatistics
{

    public class TrackingStream : Stream
    {
        private readonly Stream _baseStream;

        // 记录上传和下载的字节数
        public long TotalRead { get; private set; }
        public long TotalWritten { get; private set; }

        public TrackingStream(Stream baseStream)
        {
            _baseStream = baseStream ?? throw new ArgumentNullException(nameof(baseStream));
        }

        // --- 核心统计逻辑：拦截读取（上传） ---
        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = _baseStream.Read(buffer, offset, count);
            TotalRead += read;
            return read;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            int read = await _baseStream.ReadAsync(buffer, offset, count, cancellationToken);
            TotalRead += read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read = await _baseStream.ReadAsync(buffer, cancellationToken);
            TotalRead += read;
            return read;
        }

        // --- 核心统计逻辑：拦截写入（下载） ---
        public override void Write(byte[] buffer, int offset, int count)
        {
            _baseStream.Write(buffer, offset, count);
            TotalWritten += count;
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await _baseStream.WriteAsync(buffer, offset, count, cancellationToken);
            TotalWritten += count;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await _baseStream.WriteAsync(buffer, cancellationToken);
            TotalWritten += buffer.Length;
        }

        // --- 以下是必须实现的 Stream 抽象成员（直接透传给底层流） ---
        public override bool CanRead => _baseStream.CanRead;
        public override bool CanSeek => _baseStream.CanSeek;
        public override bool CanWrite => _baseStream.CanWrite;
        public override long Length => _baseStream.Length;
        public override long Position { get => _baseStream.Position; set => _baseStream.Position = value; }
        public override void Flush() => _baseStream.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => _baseStream.FlushAsync(cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => _baseStream.Seek(offset, origin);
        public override void SetLength(long value) => _baseStream.SetLength(value);
    }
}
