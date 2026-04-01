using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace Jellyfin.Plugin.TheDiscDb.TheDiscDb;

/// <summary>
/// Computes the TheDiscDb ContentHash for a BDMV folder.
/// The hash is an MD5 of all .m2ts file sizes (as little-endian Int64 bytes),
/// sorted by filename. This matches TheDiscDb's hashing algorithm exactly.
/// </summary>
public static class ContentHashCalculator
{
    /// <summary>
    /// Computes the ContentHash for a BDMV folder.
    /// </summary>
    /// <param name="bdmvPath">Path to the directory containing the BDMV subfolder (e.g., "Season 3/").</param>
    /// <returns>Uppercase hex MD5 hash string, or null if the BDMV/STREAM directory doesn't exist.</returns>
    public static string? ComputeHash(string bdmvPath)
    {
        var streamDir = Path.Combine(bdmvPath, "BDMV", "STREAM");
        if (!Directory.Exists(streamDir))
        {
            return null;
        }

        var m2tsFiles = Directory.GetFiles(streamDir, "*.m2ts")
            .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (m2tsFiles.Length == 0)
        {
            return null;
        }

        using var md5 = MD5.Create();

        for (int i = 0; i < m2tsFiles.Length; i++)
        {
            var sizeBytes = BitConverter.GetBytes(new FileInfo(m2tsFiles[i]).Length);
            if (i < m2tsFiles.Length - 1)
            {
                md5.TransformBlock(sizeBytes, 0, sizeBytes.Length, null, 0);
            }
            else
            {
                md5.TransformFinalBlock(sizeBytes, 0, sizeBytes.Length);
            }
        }

        return BitConverter.ToString(md5.Hash!).Replace("-", string.Empty);
    }
}
