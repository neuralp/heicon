using System;
using System.Collections.Generic;
using System.IO;

namespace heicon.Services
{
    /// <summary>
    /// Expands a mix of file and folder paths into the HEIC/HEIF files they contain.
    /// </summary>
    public static class FileCollector
    {
        public static readonly IReadOnlyCollection<string> Extensions = new[] { ".heic", ".heif" };

        private static readonly HashSet<string> ExtensionSet = new(Extensions, StringComparer.OrdinalIgnoreCase);

        public static bool IsHeic(string path) => ExtensionSet.Contains(Path.GetExtension(path));

        /// <summary>
        /// Yields every HEIC/HEIF file among <paramref name="paths"/>. Folders are searched,
        /// recursively when <paramref name="recurse"/> is set. Enumeration is lazy and may touch
        /// the disk heavily, so call it off the UI thread.
        /// </summary>
        public static IEnumerable<string> Collect(IEnumerable<string> paths, bool recurse)
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = recurse,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
            };

            foreach (var path in paths)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                if (Directory.Exists(path))
                {
                    foreach (var file in Directory.EnumerateFiles(path, "*", options))
                    {
                        if (IsHeic(file))
                        {
                            yield return file;
                        }
                    }
                }
                else if (File.Exists(path) && IsHeic(path))
                {
                    yield return path;
                }
            }
        }
    }
}
