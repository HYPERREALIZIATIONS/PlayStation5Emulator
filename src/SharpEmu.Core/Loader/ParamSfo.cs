using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SharpEmu.Core.Loader;

/// <summary>
/// Parser for the PlayStation param.sfo (PSF) metadata file. PS5 games ship a
/// sce_sys/param.sfo describing title, version, title id, and more. Format is
/// the standard PSF structure documented publicly.
/// </summary>
public static class ParamSfo
{
    // PSF data format codes
    private const ushort FMT_BINARY = 0x0000;
    private const ushort FMT_UTF8 = 0x0204;
    private const ushort FMT_UTF8_SPECIAL = 0x0209;
    private const ushort FMT_INT32 = 0x0404;

    public static Dictionary<string, string> Parse(byte[] data)
    {
        var result = new Dictionary<string, string>();
        if (data.Length < 0x14) return result;

        // Header: magic "PSF", version, key_off, data_off, entries
        string magic = Encoding.ASCII.GetString(data, 0, 3);
        if (magic != "PSF") return result;

        uint keyTableStart = ReadU32LE(data, 8);
        uint dataTableStart = ReadU32LE(data, 12);
        uint entries = ReadU32LE(data, 16);

        for (uint i = 0; i < entries; i++)
        {
            int idxOff = 0x14 + (int)(i * 0x10);
            if (idxOff + 0x10 > data.Length) break;

            ushort keyOffset = ReadU16LE(data, idxOff);
            ushort fmt = ReadU16LE(data, idxOff + 2);
            uint dataLen = ReadU32LE(data, idxOff + 4);
            uint dataMax = ReadU32LE(data, idxOff + 8);
            uint dataOffset = ReadU32LE(data, idxOff + 12);

            int keyPos = (int)(keyTableStart + keyOffset);
            string key = ReadCString(data, keyPos);
            int dataPos = (int)(dataTableStart + dataOffset);

            if (fmt == FMT_UTF8 || fmt == FMT_UTF8_SPECIAL)
            {
                string val = ReadCString(data, dataPos);
                result[key] = val;
            }
            else if (fmt == FMT_INT32)
            {
                if (dataPos + 4 <= data.Length)
                    result[key] = ReadU32LE(data, dataPos).ToString();
            }
            // binary entries are skipped for display purposes
        }
        return result;
    }

    private static string ReadCString(byte[] data, int off)
    {
        if (off < 0 || off >= data.Length) return "";
        int end = off;
        while (end < data.Length && data[end] != 0) end++;
        return Encoding.UTF8.GetString(data, off, end - off);
    }

    private static ushort ReadU16LE(byte[] d, int off) => (ushort)(d[off] | (d[off + 1] << 8));
    private static uint ReadU32LE(byte[] d, int off) => (uint)(d[off] | (d[off + 1] << 8) | (d[off + 2] << 16) | (d[off + 3] << 24));
}
