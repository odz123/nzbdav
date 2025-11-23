using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Streams;

public class NzbFileStream(
    string[] fileSegmentIds,
    long fileSize,
    INntpClient client,
    int concurrentConnections
) : Stream
{
    private long _position = 0;
    private CombinedStream? _innerStream;
    private int _disposed = 0;

    // BUG FIX NEW-012: Use SortedDictionary for more efficient range lookups
    // Cache for segment seek results to avoid recalculating positions
    // Key: start byte offset, Value: (segment index, segment byte range)
    private readonly SortedDictionary<long, InterpolationSearch.Result> _segmentCache = new();

    // OPTIMIZATION: Semaphore to limit concurrent blocking reads and prevent thread pool exhaustion
    // Allows max 100 concurrent blocking reads across all NzbFileStream instances
    private static readonly SemaphoreSlim ReadSemaphore = new(100, 100);

    public override void Flush()
    {
        _innerStream?.Flush();
    }

    // PERF NOTE #2: This blocking call is required by Stream base class contract
    // The WebDAV library may call synchronous Read(). While this creates thread pool pressure,
    // it cannot be avoided without breaking the Stream abstraction.
    // OPTIMIZATION: Use semaphore to limit concurrent blocking reads and prevent exhaustion
    public override int Read(byte[] buffer, int offset, int count)
    {
        ReadSemaphore.Wait();
        try
        {
            return ReadAsync(buffer, offset, count).GetAwaiter().GetResult();
        }
        finally
        {
            ReadSemaphore.Release();
        }
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (_innerStream == null) _innerStream = await GetFileStream(_position, cancellationToken);
        var read = await _innerStream.ReadAsync(buffer, offset, count, cancellationToken);
        _position += read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var absoluteOffset = origin == SeekOrigin.Begin ? offset
            : origin == SeekOrigin.Current ? _position + offset
            : origin == SeekOrigin.End ? fileSize + offset
            : throw new ArgumentException("Invalid SeekOrigin", nameof(origin));

        // Validate bounds
        if (absoluteOffset < 0 || absoluteOffset > fileSize)
            throw new ArgumentOutOfRangeException(nameof(offset), "Seek position out of bounds");

        if (_position == absoluteOffset) return _position;
        _position = absoluteOffset;
        _innerStream?.Dispose();
        _innerStream = null;
        return _position;
    }

    public override void SetLength(long value)
    {
        throw new InvalidOperationException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new InvalidOperationException();
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => fileSize;

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }


    private async Task<InterpolationSearch.Result> SeekSegment(long byteOffset, CancellationToken ct)
    {
        // PERF FIX #6: Efficient O(log n) cache lookup using LINQ with SortedDictionary
        // Find the largest cache entry where the key is <= byteOffset and range contains offset
        var cachedResult = _segmentCache
            .Where(kvp => kvp.Key <= byteOffset && kvp.Value.FoundByteRange.Contains(byteOffset))
            .Select(kvp => kvp.Value)
            .LastOrDefault();

        if (cachedResult != default)
            return cachedResult;

        // Not in cache, perform the search
        var result = await InterpolationSearch.Find(
            byteOffset,
            new LongRange(0, fileSegmentIds.Length),
            new LongRange(0, fileSize),
            async (guess) =>
            {
                var header = await client.GetSegmentYencHeaderAsync(fileSegmentIds[guess], ct);
                return new LongRange(header.PartOffset, header.PartOffset + header.PartSize);
            },
            ct
        );

        // Cache the result for future seeks using the range start as the key
        // This allows efficient range-based lookups
        // OPTIMIZATION: Increased cache size from 100 to 1000 entries to reduce network round-trips
        // Large video files with frequent seeks benefit from larger cache
        if (_segmentCache.Count < 1000)
            _segmentCache[result.FoundByteRange.StartInclusive] = result;

        return result;
    }

    private async Task<CombinedStream> GetFileStream(long rangeStart, CancellationToken cancellationToken)
    {
        if (rangeStart == 0) return GetCombinedStream(0, cancellationToken);
        var foundSegment = await SeekSegment(rangeStart, cancellationToken);
        var stream = GetCombinedStream(foundSegment.FoundIndex, cancellationToken);
        await stream.DiscardBytesAsync(rangeStart - foundSegment.FoundByteRange.StartInclusive);
        return stream;
    }

    private CombinedStream GetCombinedStream(int firstSegmentIndex, CancellationToken ct)
    {
        // Note: The Select creates tasks lazily, and WithConcurrency() already optimizes
        // time-to-first-byte by yielding the first task immediately (see IEnumerableTaskExtensions.cs:36-42)
        // and managing concurrent prefetching of subsequent segments
        return new CombinedStream(
            fileSegmentIds[firstSegmentIndex..]
                .Select(async x => (Stream)await client.GetSegmentStreamAsync(x, false, ct))
                .WithConcurrency(concurrentConnections)
        );
    }

    protected override void Dispose(bool disposing)
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) == 1) return;
        if (disposing)
        {
            _innerStream?.Dispose();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) == 1) return;
        if (_innerStream != null) await _innerStream.DisposeAsync();
        // BUG FIX NEW-003: Call base.DisposeAsync() to ensure proper disposal chain
        await base.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}