using System.Buffers;

namespace NzbWebDAV.Streams;

public class CombinedStream(IEnumerable<Task<Stream>> streams) : Stream
{
    private readonly IEnumerator<Task<Stream>> _streams = streams.GetEnumerator();
    private Stream? _currentStream;
    private long _position;
    private int _isDisposed = 0;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        return ReadAsync(buffer, offset, count).GetAwaiter().GetResult();
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (count == 0) return 0;
        while (true) // HIGH-2 FIX: Use explicit cancellation check instead of loop condition
        {
            // HIGH-2 FIX: Explicit cancellation check throws OperationCanceledException
            // This is better than silently returning 0 on cancellation
            cancellationToken.ThrowIfCancellationRequested();

            // If we haven't read the first stream, read it.
            if (_currentStream == null)
            {
                if (!_streams.MoveNext()) return 0;
                _currentStream = await _streams.Current;
            }

            // read from our current stream
            var readCount = await _currentStream.ReadAsync
            (
                buffer.AsMemory(offset, count),
                cancellationToken
            );
            _position += readCount;
            if (readCount > 0) return readCount;

            // If we couldn't read anything from our current stream,
            // it's time to advance to the next stream.
            await _currentStream.DisposeAsync();
            if (!_streams.MoveNext()) return 0;
            _currentStream = await _streams.Current;
        }
    }

    public async Task DiscardBytesAsync(long count)
    {
        if (count == 0) return;
        var remaining = count;
        // Increased from 1KB to 256KB for much faster seeking
        // When seeking within a segment, we need to discard unwanted bytes
        // Larger buffer = fewer read operations = faster seeks
        // Use ArrayPool to avoid heap allocation of large buffers
        const int bufferSize = 256 * 1024;
        var throwaway = ArrayPool<byte>.Shared.Rent(bufferSize);
        try
        {
            while (remaining > 0)
            {
                var toRead = (int)Math.Min(remaining, throwaway.Length);
                var read = await ReadAsync(throwaway, 0, toRead);
                remaining -= read;
                if (read == 0) break;
            }

            // Note: Position already updated in ReadAsync, so no need to update here
            // ReadAsync increments _position by the bytes actually read
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(throwaway);
        }
    }

    public override void Flush()
    {
        throw new NotSupportedException();
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException();
    }

    protected override void Dispose(bool disposing)
    {
        if (Interlocked.CompareExchange(ref _isDisposed, 1, 0) == 1) return;
        if (disposing)
        {
            _streams.Dispose();
            _currentStream?.Dispose();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _isDisposed, 1, 0) == 1) return;

        try
        {
            // MEDIUM-1 FIX: Dispose current stream
            if (_currentStream != null)
            {
                await _currentStream.DisposeAsync();
                _currentStream = null;
            }

            // MEDIUM-1 FIX: Dispose any remaining streams in the enumerator
            // This handles pre-fetched Task<Stream> items that haven't been consumed yet
            var remainingStreamTasks = new List<Task<Stream>>();
            while (_streams.MoveNext())
            {
                remainingStreamTasks.Add(_streams.Current);
            }

            // Dispose the enumerator first
            _streams.Dispose();

            // Now await and dispose the remaining streams with timeout
            foreach (var streamTask in remainingStreamTasks)
            {
                try
                {
                    // Give each stream a short timeout to avoid hanging
                    // Use Task.WaitAsync (available in .NET 6+)
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    var stream = await streamTask.WaitAsync(cts.Token);
                    await stream.DisposeAsync();
                }
                catch (OperationCanceledException)
                {
                    // Stream task timed out - log but continue
                    System.Diagnostics.Debug.WriteLine("Stream task timed out during disposal");
                }
                catch (Exception ex)
                {
                    // Best effort cleanup - log but don't fail
                    System.Diagnostics.Debug.WriteLine($"Error disposing stream: {ex.Message}");
                }
            }

            GC.SuppressFinalize(this);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"CombinedStream disposal error: {ex.Message}");
        }
    }
}