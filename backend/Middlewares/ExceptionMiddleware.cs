using Microsoft.AspNetCore.Http;
using NWebDav.Server.Helpers;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using Serilog;

namespace NzbWebDAV.Middlewares;

public class ExceptionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // If the response has not started, we can write our custom response
            if (!context.Response.HasStarted)
            {
                context.Response.Clear();
                context.Response.StatusCode = 499; // Non-standard status code for client closed request
                await context.Response.WriteAsync("Client closed request.");
            }
        }
        catch (UsenetArticleNotFoundException e)
        {
            if (!context.Response.HasStarted)
            {
                context.Response.Clear();
                context.Response.StatusCode = 404;
            }

            var filePath = GetRequestFilePath(context);
            // Log with exception parameter to include stack trace if needed for debugging
            Log.Error(e, "File {FilePath} has missing articles: {SegmentId}", filePath, e.SegmentId);
        }
        catch (SeekPositionNotFoundException e)
        {
            if (!context.Response.HasStarted)
            {
                context.Response.Clear();
                context.Response.StatusCode = 404;
            }

            var filePath = GetRequestFilePath(context);
            var seekPosition = context.Request.GetRange()?.Start?.ToString() ?? "unknown";
            // Log with exception parameter to include stack trace
            Log.Error(e, "File {FilePath} could not seek to byte position: {SeekPosition}", filePath, seekPosition);
        }
        catch (Exception e) when (IsDavItemRequest(context))
        {
            if (!context.Response.HasStarted)
            {
                context.Response.Clear();
                context.Response.StatusCode = 500;
            }

            var filePath = GetRequestFilePath(context);

            // CRITICAL FIX: Log full exception with stack trace for debugging
            // Include request method and path for better diagnostics
            Log.Error(e,
                "Unhandled exception serving WebDAV file {FilePath}. " +
                "Type: {ExceptionType}, Request: {Method} {Path}",
                filePath, e.GetType().Name, context.Request.Method, context.Request.Path);

            // Re-throw critical exceptions that should crash the app
            // These indicate serious issues that require immediate attention
            if (e is OutOfMemoryException || e is StackOverflowException)
            {
                Log.Fatal(e, "Critical exception occurred - application will terminate");
                throw;
            }

            // For database errors, log additional context
            // This helps identify database corruption or connection issues quickly
            if (e.GetType().Namespace?.StartsWith("Microsoft.EntityFrameworkCore") == true)
            {
                Log.Error("Database error detected - this may indicate database corruption or connection issues. " +
                         "Check database health and connection pool.");
            }
        }
    }

    private static string GetRequestFilePath(HttpContext context)
    {
        return context.Items["DavItem"] is DavItem davItem
            ? davItem.Path
            : context.Request.Path;
    }

    private static bool IsDavItemRequest(HttpContext context)
    {
        return context.Items["DavItem"] is DavItem;
    }
}