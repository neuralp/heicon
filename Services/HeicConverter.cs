using System;
using System.IO;
using System.Threading;
using heicon.Models;
using ImageMagick;

namespace heicon.Services
{
    public enum OutputFormat
    {
        Jpeg,
        Png,
    }

    public sealed record ConversionOptions(OutputFormat Format, int JpegQuality, bool DeleteSource);

    public sealed record ConversionResult(ConversionStatus Status, string Message, string? OutputPath = null);

    /// <summary>
    /// Converts a single HEIC/HEIF file to JPEG or PNG beside the original.
    /// </summary>
    public static class HeicConverter
    {
        public const string JpegExtension = ".jpg";
        public const string PngExtension = ".png";

        public static string GetOutputPath(string sourcePath, OutputFormat format) =>
            Path.ChangeExtension(sourcePath, format == OutputFormat.Jpeg ? JpegExtension : PngExtension);

        /// <summary>
        /// Converts <paramref name="sourcePath"/>. This method blocks, so run it on a worker thread.
        /// Cancellation is only checked before work starts; once a file is being decoded it is
        /// allowed to finish so we never leave a half-done conversion behind.
        /// </summary>
        public static ConversionResult Convert(string sourcePath, ConversionOptions options, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!File.Exists(sourcePath))
            {
                return new ConversionResult(ConversionStatus.Failed, "Source file no longer exists");
            }

            var outputPath = GetOutputPath(sourcePath, options.Format);
            if (File.Exists(outputPath))
            {
                return new ConversionResult(ConversionStatus.Skipped, $"{Path.GetFileName(outputPath)} already exists", outputPath);
            }

            // Write to a uniquely named temp file first, then move it into place. A failed
            // or interrupted conversion can never leave a truncated image at the output path.
            var directory = Path.GetDirectoryName(outputPath) ?? string.Empty;
            var tempPath = Path.Combine(directory, $".{Path.GetFileName(outputPath)}.{Guid.NewGuid():N}.heicon.tmp");

            try
            {
                using (var image = new MagickImage(sourcePath))
                {
                    image.AutoOrient();

                    MagickFormat format;
                    if (options.Format == OutputFormat.Jpeg)
                    {
                        format = MagickFormat.Jpeg;
                        image.Quality = (uint)Math.Clamp(options.JpegQuality, 1, 100);
                    }
                    else
                    {
                        format = MagickFormat.Png;
                    }

                    image.Write(tempPath, format);
                }

                try
                {
                    File.Move(tempPath, outputPath, overwrite: false);
                }
                catch (IOException) when (File.Exists(outputPath))
                {
                    // Another file (e.g. IMG.heif next to IMG.heic) claimed this name first.
                    return new ConversionResult(ConversionStatus.Skipped, $"{Path.GetFileName(outputPath)} already exists", outputPath);
                }

                CopyTimestamps(sourcePath, outputPath);
            }
            catch (MagickException ex)
            {
                return new ConversionResult(ConversionStatus.Failed, $"Couldn't read image: {CleanMagickMessage(ex.Message)}");
            }
            catch (Exception ex)
            {
                return new ConversionResult(ConversionStatus.Failed, ex.Message);
            }
            finally
            {
                TryDelete(tempPath);
            }

            if (!options.DeleteSource)
            {
                return new ConversionResult(ConversionStatus.Done, $"Saved {Path.GetFileName(outputPath)}", outputPath);
            }

            try
            {
                var output = new FileInfo(outputPath);
                if (!output.Exists || output.Length == 0)
                {
                    return new ConversionResult(ConversionStatus.Failed, "Converted file could not be verified; original kept", outputPath);
                }

                var source = new FileInfo(sourcePath);
                if (source.IsReadOnly)
                {
                    source.IsReadOnly = false;
                }

                source.Delete();
                return new ConversionResult(ConversionStatus.Done, $"Replaced with {Path.GetFileName(outputPath)}", outputPath);
            }
            catch (Exception ex)
            {
                return new ConversionResult(ConversionStatus.Failed, $"Saved {Path.GetFileName(outputPath)}, but the original could not be deleted: {ex.Message}", outputPath);
            }
        }

        /// <summary>
        /// ImageMagick messages look like "reason `C:\full\path.heic' @ error/heic.c/ReadHEICImage/1029";
        /// keep just the reason for display.
        /// </summary>
        private static string CleanMagickMessage(string message)
        {
            var cut = message.IndexOf(" `", StringComparison.Ordinal);
            if (cut < 0)
            {
                cut = message.IndexOf(" @ ", StringComparison.Ordinal);
            }

            return cut > 0 ? message[..cut] : message;
        }

        private static void CopyTimestamps(string sourcePath, string outputPath)
        {
            try
            {
                File.SetCreationTimeUtc(outputPath, File.GetCreationTimeUtc(sourcePath));
                File.SetLastWriteTimeUtc(outputPath, File.GetLastWriteTimeUtc(sourcePath));
            }
            catch (Exception)
            {
                // Timestamps are a nicety; the conversion itself succeeded.
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception)
            {
                // Best effort cleanup of the temp file.
            }
        }
    }
}
