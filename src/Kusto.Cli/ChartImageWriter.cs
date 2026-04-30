using System.Globalization;

namespace Kusto.Cli;

internal static class ChartImageWriter
{
    public static async Task<string> WritePngAsync(
        QueryChartDefinition chart,
        string path,
        int width,
        int height,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chart);
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new UserFacingException("--output-chart requires a non-empty file path.");
        }

        var extension = Path.GetExtension(path);
        if (!string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase))
        {
            throw new UserFacingException($"--output-chart requires a path ending in .png; got '{(string.IsNullOrEmpty(extension) ? "(no extension)" : extension)}'.");
        }

        if (width < ChartStyle.MinDimension || width > ChartStyle.MaxDimension)
        {
            throw new UserFacingException(string.Format(
                CultureInfo.InvariantCulture,
                "--output-chart-width must be between {0} and {1} pixels.",
                ChartStyle.MinDimension,
                ChartStyle.MaxDimension));
        }

        if (height < ChartStyle.MinDimension || height > ChartStyle.MaxDimension)
        {
            throw new UserFacingException(string.Format(
                CultureInfo.InvariantCulture,
                "--output-chart-height must be between {0} and {1} pixels.",
                ChartStyle.MinDimension,
                ChartStyle.MaxDimension));
        }

        var fullPath = Path.GetFullPath(path);

        if (Directory.Exists(fullPath))
        {
            throw new UserFacingException($"--output-chart target '{fullPath}' is an existing directory.");
        }

        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            try
            {
                Directory.CreateDirectory(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new UserFacingException($"Couldn't create directory '{directory}' for chart output: {ex.Message}");
            }
        }

        var tempPath = fullPath + ".tmp";
        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                ImageChartRenderer.RenderPng(chart, width, height, stream);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(tempPath, fullPath, overwrite: true);
        }
        catch
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
            }
            throw;
        }

        return fullPath;
    }
}

