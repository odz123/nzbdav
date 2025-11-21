// ReSharper disable InconsistentNaming

namespace NzbWebDAV.Extensions;

public static class IEnumerableTaskExtensions
{
    /// <summary>
    /// Executes tasks with specified concurrency and enumerates results as they come in
    /// </summary>
    /// <param name="tasks">The tasks to execute</param>
    /// <param name="concurrency">The max concurrency</param>
    /// <param name="cancellationToken">The cancellation token</param>
    /// <typeparam name="T">The resulting type of each task</typeparam>
    /// <returns>An IAsyncEnumerable that enumerates task results as they come in</returns>
    public static IEnumerable<Task<T>> WithConcurrency<T>
    (
        this IEnumerable<Task<T>> tasks,
        int concurrency
    ) where T : IDisposable
    {
        if (concurrency < 1)
            throw new ArgumentException("concurrency must be greater than zero.");

        if (concurrency == 1)
        {
            foreach (var task in tasks) yield return task;
            yield break;
        }

        var isFirst = true;
        var runningTasks = new Queue<Task<T>>();
        try
        {
            foreach (var task in tasks)
            {
                if (isFirst)
                {
                    // help with time-to-first-byte
                    yield return task;
                    isFirst = false;
                    continue;
                }

                runningTasks.Enqueue(task);
                if (runningTasks.Count < concurrency) continue;
                yield return runningTasks.Dequeue();
            }

            while (runningTasks.Count > 0)
                yield return runningTasks.Dequeue();
        }
        finally
        {
            // Clean up any tasks that weren't yielded to the caller
            while (runningTasks.Count > 0)
            {
                var task = runningTasks.Dequeue();
                try
                {
                    // Wait for task to complete with timeout to prevent hanging
                    // This is a synchronous wait in finally block to ensure cleanup completes
                    task.Wait(TimeSpan.FromSeconds(5));

                    // Dispose result if task completed successfully
                    if (task.Status == TaskStatus.RanToCompletion && task.Result != null)
                    {
                        task.Result.Dispose();
                    }
                    // If task faulted or was canceled, exception is observed by Wait()
                }
                catch (Exception)
                {
                    // Swallow all exceptions during cleanup:
                    // - Task exceptions (faulted/canceled) - already logged elsewhere
                    // - Disposal exceptions - best effort cleanup
                    // - Timeout exceptions - prevent hanging indefinitely
                }
            }
        }
    }

    public static async IAsyncEnumerable<T> WithConcurrencyAsync<T>
    (
        this IEnumerable<Task<T>> tasks,
        int concurrency
    )
    {
        if (concurrency < 1)
            throw new ArgumentException("concurrency must be greater than zero.");

        var runningTasks = new HashSet<Task<T>>();
        foreach (var task in tasks)
        {
            runningTasks.Add(task);
            if (runningTasks.Count < concurrency) continue;
            var completedTask = await Task.WhenAny(runningTasks);
            runningTasks.Remove(completedTask);
            yield return await completedTask;
        }

        while (runningTasks.Count > 0)
        {
            var completedTask = await Task.WhenAny(runningTasks);
            runningTasks.Remove(completedTask);
            yield return await completedTask;
        }
    }
}