namespace Pirsa.Services;

/// <summary>Small, best-effort disk usage helpers for the Settings data page.</summary>
public static class StorageUsage
{
    /// <summary>Total bytes under a file or folder; zero when it does not exist.</summary>
    public static long SizeOf(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                return new FileInfo(path).Length;
            }

            if (!Directory.Exists(path))
            {
                return 0;
            }

            long total = 0;
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    total += new FileInfo(file).Length;
                }
                catch
                {
                    // A file can disappear while the cache is being read.
                }
            }

            return total;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Human form used by Settings, e.g. "0 KB" or "1.4 MB".</summary>
    public static string Format(long bytes)
    {
        if (bytes <= 0)
        {
            return "0 KB";
        }

        const double kilobyte = 1024;
        var value = (double)bytes;

        if (value >= kilobyte * kilobyte * 1024)
        {
            return $"{value / (kilobyte * kilobyte * 1024):0.#} GB";
        }

        return value >= kilobyte * kilobyte
            ? $"{value / (kilobyte * kilobyte):0.#} MB"
            : $"{Math.Max(1, value / kilobyte):0} KB";
    }

    /// <summary>Deletes the temporary cache contents and recreates the folder.</summary>
    public static void ClearDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(path))
                {
                    try
                    {
                        if (Directory.Exists(entry))
                        {
                            Directory.Delete(entry, recursive: true);
                        }
                        else
                        {
                            File.Delete(entry);
                        }
                    }
                    catch
                    {
                        // A cache file may still be open; leave it for next time.
                    }
                }
            }
            else
            {
                Directory.CreateDirectory(path);
            }
        }
        catch
        {
            // Storage cleanup is best effort.
        }
    }
}
