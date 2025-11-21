using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Streams;
using Usenet.Nntp;
using Usenet.Nntp.Models;
using Usenet.Nntp.Responses;
using Usenet.Nzb;
using Usenet.Yenc;

namespace NzbWebDAV.Clients.Usenet;

public class ThreadSafeNntpClient : INntpClient
{
    private readonly NntpConnection _connection;
    private readonly NntpClient _client;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    // Compiled regex for better performance when detecting article not found errors
    private static readonly System.Text.RegularExpressions.Regex ArticleNotFound423Regex =
        new(@"(\b|^|\[|\()423(\b|$|\]|\)|:|\s)", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    private static readonly System.Text.RegularExpressions.Regex ArticleNotFound430Regex =
        new(@"(\b|^|\[|\()430(\b|$|\]|\)|:|\s)", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    public ThreadSafeNntpClient()
    {
        _connection = new NntpConnection();
        _client = new NntpClient(_connection);
    }

    public Task<bool> ConnectAsync(string host, int port, bool useSsl, CancellationToken cancellationToken)
    {
        return Synchronized(() => _client.ConnectAsync(host, port, useSsl), cancellationToken);
    }

    public Task<bool> AuthenticateAsync(string user, string pass, CancellationToken cancellationToken)
    {
        return Synchronized(() => _client.Authenticate(user, pass), cancellationToken);
    }

    public Task<NntpStatResponse> StatAsync(string segmentId, CancellationToken cancellationToken)
    {
        return Synchronized(() =>
        {
            try
            {
                var response = _client.Stat(new NntpMessageId(segmentId));

                // Throw exception if article not found, so multi-server failover works
                if (response.ResponseType == NntpStatResponseType.NoArticleWithThatNumber ||
                    response.ResponseType == NntpStatResponseType.NoArticleWithThatMessageId)
                {
                    throw new UsenetArticleNotFoundException(segmentId);
                }

                return response;
            }
            catch (UsenetArticleNotFoundException)
            {
                // Article definitely not found - rethrow as-is for multi-server failover
                throw;
            }
            catch (global::Usenet.Exceptions.NntpException ex)
            {
                // Check if this is an "article not found" error based on NNTP error codes/messages
                if (IsArticleNotFoundError(ex.Message))
                {
                    throw new UsenetArticleNotFoundException(segmentId);
                }

                // For other NNTP errors, rethrow so they can be handled by retry logic
                throw;
            }
        }, cancellationToken);
    }

    public Task<NntpDateResponse> DateAsync(CancellationToken cancellationToken)
    {
        return Synchronized(() => _client.Date(), cancellationToken);
    }

    public Task<UsenetArticleHeaders> GetArticleHeadersAsync(string segmentId, CancellationToken cancellationToken)
    {
        return Synchronized(() =>
        {
            try
            {
                var headResponse = _client.Head(new NntpMessageId(segmentId));

                // Throw exception if article not found, so multi-server failover works
                if (headResponse == null || !headResponse.Success || headResponse.Article?.Headers == null)
                {
                    throw new UsenetArticleNotFoundException(segmentId);
                }

                return new UsenetArticleHeaders(headResponse.Article.Headers);
            }
            catch (UsenetArticleNotFoundException)
            {
                // Article definitely not found - rethrow as-is for multi-server failover
                throw;
            }
            catch (global::Usenet.Exceptions.NntpException ex)
            {
                // Check if this is an "article not found" error based on NNTP error codes/messages
                if (IsArticleNotFoundError(ex.Message))
                {
                    throw new UsenetArticleNotFoundException(segmentId);
                }

                // For other NNTP errors, rethrow so they can be handled by retry logic
                throw;
            }
        }, cancellationToken);
    }

    public async Task<YencHeaderStream> GetSegmentStreamAsync
    (
        string segmentId,
        bool includeHeaders,
        CancellationToken cancellationToken
    )
    {
        await _semaphore.WaitAsync(cancellationToken);
        return await Task.Run(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var article = GetArticle(segmentId, includeHeaders);
                var stream = YencStreamDecoder.Decode(article.Body);
                // Increased buffer size from 1KB default to 64KB for better streaming throughput
                // This reduces overhead and improves performance for large segment transfers
                return new YencHeaderStream(
                    stream.Header,
                    article.Headers,
                    new BufferToEndStream(stream.OnDispose(OnDispose), minimumSegmentSize: 64 * 1024)
                );

                // we only want to release the semaphore once the stream is disposed.
                void OnDispose() => _semaphore.Release();
            }
            catch (Exception)
            {
                // or if there is an error getting the stream itself.
                _semaphore.Release();
                throw;
            }
        });
    }

    public async Task<YencHeader> GetSegmentYencHeaderAsync(string segmentId, CancellationToken cancellationToken)
    {
        await using var stream = await GetSegmentStreamAsync(segmentId, false, cancellationToken);
        return stream.Header;
    }

    public async Task<long> GetFileSizeAsync(NzbFile file, CancellationToken cancellationToken)
    {
        if (file.Segments.Count == 0) return 0;
        var header = await GetSegmentYencHeaderAsync(file.Segments[^1].MessageId.Value, cancellationToken);
        return header.PartOffset + header.PartSize;
    }

    public async Task WaitForReady(CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken);
        _semaphore.Release();
    }

    private Task<T> Synchronized<T>(Func<T> run, CancellationToken cancellationToken)
    {
        return Synchronized(() => Task.Run(run, cancellationToken), cancellationToken);
    }

    private async Task<T> Synchronized<T>(Func<Task<T>> run, CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            return await run();
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private static bool IsArticleNotFoundError(string errorMessage)
    {
        if (string.IsNullOrWhiteSpace(errorMessage))
            return false;

        var message = errorMessage.ToLowerInvariant();

        // Check for common "article not found" phrases
        if (message.Contains("no article") ||
            message.Contains("article not found") ||
            message.Contains("no such article"))
        {
            return true;
        }

        // Check for NNTP error codes with better precision using compiled regex
        // 423: No article with that number
        // 430: No article with that message-id
        // Match patterns like: "423 ", " 423", "[423]", "(423)", "error 423", etc.
        if (ArticleNotFound423Regex.IsMatch(message) || ArticleNotFound430Regex.IsMatch(message))
        {
            return true;
        }

        return false;
    }

    private UsenetArticle GetArticle(string segmentId, bool includeHeaders)
    {
        if (includeHeaders)
        {
            try
            {
                var articleResponse = _client.Article(new NntpMessageId(segmentId));

                // Throw exception if article not found, so multi-server failover works
                if (articleResponse == null || !articleResponse.Success || articleResponse.Article?.Body == null)
                {
                    throw new UsenetArticleNotFoundException(segmentId);
                }

                return new UsenetArticle()
                {
                    Headers = new UsenetArticleHeaders(articleResponse.Article.Headers),
                    Body = articleResponse.Article.Body
                };
            }
            catch (UsenetArticleNotFoundException)
            {
                // Article definitely not found - rethrow as-is for multi-server failover
                throw;
            }
            catch (global::Usenet.Exceptions.NntpException ex)
            {
                // Check if this is an "article not found" error based on NNTP error codes/messages
                if (IsArticleNotFoundError(ex.Message))
                {
                    throw new UsenetArticleNotFoundException(segmentId);
                }

                // For other NNTP errors, rethrow so they can be handled by retry logic
                throw;
            }
        }

        try
        {
            var bodyResponse = _client.Body(new NntpMessageId(segmentId));

            // Throw exception if article not found, so multi-server failover works
            if (bodyResponse == null || !bodyResponse.Success || bodyResponse.Article?.Body == null)
            {
                throw new UsenetArticleNotFoundException(segmentId);
            }

            return new UsenetArticle()
            {
                Headers = null,
                Body = bodyResponse.Article.Body
            };
        }
        catch (UsenetArticleNotFoundException)
        {
            // Article definitely not found - rethrow as-is for multi-server failover
            throw;
        }
        catch (global::Usenet.Exceptions.NntpException ex)
        {
            // Check if this is an "article not found" error based on NNTP error codes/messages
            if (IsArticleNotFoundError(ex.Message))
            {
                throw new UsenetArticleNotFoundException(segmentId);
            }

            // For other NNTP errors, rethrow so they can be handled by retry logic
            throw;
        }
    }

    public void Dispose()
    {
        _semaphore.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }
}