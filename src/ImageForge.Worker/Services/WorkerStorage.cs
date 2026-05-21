namespace ImageForge.Worker.Services;

// Resolves the storage root and exposes the results folder. Mirrors what
// the API's ImageStorage does, but only the worker-side concerns (write).
// Both processes share the same physical folder on disk (in Docker via a
// shared volume), so they must agree on the layout.
public sealed class WorkerStorage
{
    public string ResultsDirectory { get; }

    public WorkerStorage(IConfiguration configuration, IHostEnvironment env)
    {
        var configured = configuration["Storage:Root"] ?? "storage";

        var root = Path.IsPathRooted(configured)
            ? configured
            : Path.GetFullPath(Path.Combine(env.ContentRootPath, configured));

        ResultsDirectory = Path.Combine(root, "results");
        Directory.CreateDirectory(ResultsDirectory);
    }

    public string BuildResultPath(string taskId, string targetFormat)
    {
        // Normalize the format into an extension: "webp" -> ".webp".
        var ext = targetFormat.StartsWith('.') ? targetFormat : "." + targetFormat;
        return Path.Combine(ResultsDirectory, taskId + ext);
    }
}
