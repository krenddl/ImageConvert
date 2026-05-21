using ImageForge.Shared.Contracts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;

namespace ImageForge.Worker.Services;

// The "what" of the worker: take a TaskMessage, load the source image,
// optionally resize, re-encode in the requested format, write to results/.
// Pure CPU work via ImageSharp - no external state.
public sealed class ImageProcessor
{
    private readonly ILogger<ImageProcessor> _logger;
    private readonly WorkerStorage _storage;

    public ImageProcessor(WorkerStorage storage, ILogger<ImageProcessor> logger)
    {
        _storage = storage;
        _logger = logger;
    }

    public async Task<string> ProcessAsync(TaskMessage message, CancellationToken ct)
    {
        // 1. Decode the source image from disk.
        using var image = await Image.LoadAsync(message.SourcePath, ct);

        var originalWidth = image.Width;
        var originalHeight = image.Height;

        // 2. Resize down to MaxDimension if either side exceeds it. ResizeMode.Max
        //    fits the image into the box keeping aspect ratio, never upscaling.
        if (message.MaxDimension is int max && (originalWidth > max || originalHeight > max))
        {
            image.Mutate(x => x.Resize(new ResizeOptions
            {
                Size = new Size(max, max),
                Mode = ResizeMode.Max
            }));
        }

        // 3. Pick the encoder matching the requested target format.
        IImageEncoder encoder = SelectEncoder(message.TargetFormat);

        // 4. Persist the result. The file extension matches the encoder
        //    so the OS / browser can recognise the format from the name.
        var resultPath = _storage.BuildResultPath(message.TaskId, message.TargetFormat);
        await image.SaveAsync(resultPath, encoder, ct);

        _logger.LogInformation(
            "Processed task {TaskId}: {OriginalW}x{OriginalH} -> {NewW}x{NewH} as {Format} ({Path})",
            message.TaskId, originalWidth, originalHeight, image.Width, image.Height,
            message.TargetFormat, resultPath);

        return resultPath;
    }

    private static IImageEncoder SelectEncoder(string targetFormat)
    {
        return targetFormat.ToLowerInvariant() switch
        {
            "webp" => new WebpEncoder { Quality = 80 },
            "jpg" or "jpeg" => new JpegEncoder { Quality = 80 },
            "png" => new PngEncoder(),
            _ => throw new NotSupportedException($"Target format '{targetFormat}' is not supported.")
        };
    }
}
