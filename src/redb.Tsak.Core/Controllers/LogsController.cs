using System.IO.Compression;
using redb.Route.Controllers;
using redb.Route.Controllers.Attributes;
using redb.Tsak.Contracts;
using redb.Tsak.Core.Monitoring;
using redb.Tsak.Core.Security;

namespace redb.Tsak.Core.Controllers;

/// <summary>
/// Recent log access from in-memory ring buffer + log file management.
/// GET /api/logs?afterId=-1&amp;limit=500&amp;level=Error — incremental log entries
/// GET /api/logs/files — list available log files
/// GET /api/logs/files/{filename} — download log file as ZIP
/// </summary>
[Route("/api/logs")]
[RequiresRole(TsakRoles.Operator)] // log bodies may carry payload fragments
public class LogsController : RedbController
{
    [HttpGet("")]
    public object GetLogs(
        [FromQuery("afterId")] long? afterId,
        [FromQuery("limit")] int? limit,
        [FromQuery("level")] string? level)
    {
        var buffer = Context.GetService<LogRingBuffer>();
        if (buffer is null)
            return new LogsResponse { Available = false, Entries = [] };

        var entries = afterId.HasValue
            ? buffer.GetAfter(afterId.Value, limit ?? 500, level)
            : buffer.GetRecent(limit ?? 100, level);

        return new LogsResponse
        {
            Available = true,
            Count = entries.Count,
            TotalBuffered = buffer.Count,
            BufferCapacity = buffer.Capacity,
            Entries = entries.ToArray()
        };
    }

    [HttpGet("files")]
    public object GetLogFiles()
    {
        var logsDir = Path.Combine(Directory.GetCurrentDirectory(), "Logs");
        if (!Directory.Exists(logsDir))
            return new LogFilesResponse { Available = false, Files = [] };

        var files = Directory.GetFiles(logsDir, "*.txt")
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Select(f => new LogFileInfo
            {
                Name = f.Name,
                Size = f.Length,
                LastModified = new DateTimeOffset(f.LastWriteTimeUtc, TimeSpan.Zero)
            })
            .ToArray();

        return new LogFilesResponse { Available = true, Files = files };
    }

    [HttpGet("files/{filename}")]
    public object DownloadLogFile([FromRoute("filename")] string filename)
    {
        // Sanitize filename to prevent path traversal
        var safeName = Path.GetFileName(filename);
        if (string.IsNullOrEmpty(safeName) || safeName != filename)
            return new { Error = "InvalidFilename", Message = "Invalid filename" };

        var filePath = Path.Combine(Directory.GetCurrentDirectory(), "Logs", safeName);
        if (!File.Exists(filePath))
            return new { Error = "NotFound", Message = $"Log file '{safeName}' not found" };

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            // Open with FileShare.ReadWrite to avoid conflict with Serilog's active write lock
            var entry = zip.CreateEntry(safeName, CompressionLevel.Optimal);
            using var entryStream = entry.Open();
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            fs.CopyTo(entryStream);
        }

        return ms.ToArray();
    }
}
