using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Jellyfin.Plugin.TheDiscDb.Parsers;

/// <summary>
/// Minimal MPLS (Movie PlayList) parser that extracts the clip filenames
/// referenced by each PlayItem in a Blu-ray playlist file.
/// </summary>
public static class MplsParser
{
    /// <summary>
    /// Parses an MPLS file and returns the list of .m2ts clip filenames in playlist order.
    /// </summary>
    /// <param name="mplsPath">Full path to the .mpls file.</param>
    /// <returns>List of m2ts filenames (e.g., ["00013.m2ts", "00014.m2ts"]).</returns>
    public static List<string> GetClipFiles(string mplsPath)
    {
        var clips = new List<string>();
        var data = File.ReadAllBytes(mplsPath);

        // Validate header: "MPLS" magic
        if (data.Length < 20
            || data[0] != 'M' || data[1] != 'P' || data[2] != 'L' || data[3] != 'S')
        {
            return clips;
        }

        // PlayList start address at bytes 8-11 (big-endian uint32)
        uint playlistOffset = ReadUInt32BE(data, 8);

        if (playlistOffset + 10 > data.Length)
        {
            return clips;
        }

        // PlayList section:
        //   [0-3] length (uint32)
        //   [4-5] reserved
        //   [6-7] number_of_PlayItems (uint16)
        //   [8-9] number_of_SubPaths (uint16)
        uint pos = playlistOffset;
        // uint playlistLength = ReadUInt32BE(data, pos);
        pos += 4; // skip length
        pos += 2; // skip reserved

        ushort playItemCount = ReadUInt16BE(data, pos);
        pos += 2;

        // ushort subPathCount = ReadUInt16BE(data, pos);
        pos += 2;

        // Parse each PlayItem
        for (int i = 0; i < playItemCount; i++)
        {
            if (pos + 2 > data.Length)
            {
                break;
            }

            // PlayItem length (2 bytes, does not include these 2 bytes)
            ushort itemLength = ReadUInt16BE(data, pos);
            pos += 2;

            if (pos + itemLength > data.Length || itemLength < 9)
            {
                break;
            }

            // Clip_Information_file_name: 5 ASCII chars (e.g., "00013")
            string clipName = Encoding.ASCII.GetString(data, (int)pos, 5);
            // Clip_codec_identifier: 4 ASCII chars (e.g., "M2TS") — skip

            clips.Add(clipName + ".m2ts");

            pos += (uint)itemLength;
        }

        return clips;
    }

    private static uint ReadUInt32BE(byte[] data, uint offset)
    {
        return ((uint)data[offset] << 24)
             | ((uint)data[offset + 1] << 16)
             | ((uint)data[offset + 2] << 8)
             | data[offset + 3];
    }

    private static ushort ReadUInt16BE(byte[] data, uint offset)
    {
        return (ushort)((data[offset] << 8) | data[offset + 1]);
    }
}
