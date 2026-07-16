using System;
using System.Text;
using Prospero.Emu.Core;

namespace Prospero.Emu.Format
{
    /// <summary>
    /// Extracts basic game / application info from the PT_SCE_PROCPARAM segment.
    ///
    /// PS5 (and PS4) store a SceProcessParam structure at the start of the
    /// ".sce_process_param" segment:
    ///   u32 magic            // 'ORBI' = 0x4942524F
    ///   u32 sdk_version_rev  // SDK version, little-endian dword
    ///   u64  pool_base
    ///   u32  pool_size
    ///   ... process / app params ...
    ///
    /// The same 'ORBI' magic is used on both Prospero and Orbis; this is the
    /// documented layout from OpenOrbis and the PS5 bulk-sdk-rewriter tool.
    /// </summary>
    public sealed class GameInfoExtractor
    {
        private readonly Logger _log;
        public GameInfoExtractor(Logger log) => _log = log;

        public GameInfo Extract(LoadedExecutable exe)
        {
            var info = new GameInfo { SourcePath = exe.SourcePath, Kind = exe.Kind };

            var procParam = exe.FindSegment(ElfConsts.PT_SCE_PROCPARAM);
            if (procParam != null && procParam.Data.Length > 0x10)
            {
                var r = new LeReader(procParam.Data);
                info.ProcParamMagic = r.ReadU32(0);
                _log.Debug("loader", $"procpparam magic=0x{info.ProcParamMagic:X8} (expected 0x4942524F 'ORBI')");
                if (info.ProcParamMagic == 0x4942524F)
                {
                    info.SdkVersionRaw = r.ReadU32(4);
                    info.SdkVersion = DecodeSdkVersion(info.SdkVersionRaw);
                    info.PoolBase = r.ReadU64(8);
                    info.PoolSize = r.ReadU32(16);
                    _log.Info("loader", $"SDK version: {info.SdkVersion} (raw 0x{info.SdkVersionRaw:X8})");
                }
            }

            info.TitleId = ScanForTitleId(exe);
            info.Version = ScanForVersion(exe);
            info.Comment = ScanComment(exe);

            return info;
        }

        /// <summary>
        /// SDK version is stored as a packed decimal: e.g. 0x05000000 -> 5.00,
        /// 0x07500000 -> 7.50. Layout: 0x[major][minor][rev][patch].
        /// </summary>
        public static string DecodeSdkVersion(uint raw)
        {
            int major = (int)((raw >> 24) & 0xFF);
            int minor = (int)((raw >> 16) & 0xFF);
            int rev = (int)((raw >> 8) & 0xFF);
            int patch = (int)(raw & 0xFF);
            return $"{major}.{minor:D2}{(rev != 0 ? "." + rev : "")}{(patch != 0 ? "." + patch : "")}";
        }

        private static string ScanForTitleId(LoadedExecutable exe)
        {
            foreach (var seg in exe.Segments)
            {
                var s = seg.Data;
                for (int i = 0; i + 9 < s.Length; i++)
                {
                    if (s[i] == (byte)'P' && s[i + 1] == (byte)'P' && s[i + 2] == (byte)'S' && s[i + 3] == (byte)'A')
                    {
                        string cand = Encoding.ASCII.GetString(s, i, 9);
                        if (char.IsDigit(cand[4]) && char.IsDigit(cand[5]) && char.IsDigit(cand[6]) &&
                            char.IsDigit(cand[7]) && char.IsDigit(cand[8]) &&
                            (i == 0 || s[i - 1] == 0) && (i + 9 >= s.Length || s[i + 9] == 0))
                        {
                            return cand;
                        }
                    }
                }
            }
            return "";
        }

        private static string ScanForVersion(LoadedExecutable exe)
        {
            foreach (var seg in exe.Segments)
            {
                var s = seg.Data;
                for (int i = 0; i + 4 < s.Length; i++)
                {
                    if (char.IsDigit((char)s[i]) && s[i + 1] == (byte)'.' &&
                        char.IsDigit((char)s[i + 2]) && char.IsDigit((char)s[i + 3]) &&
                        (i == 0 || s[i - 1] == 0))
                    {
                        int end = i + 2;
                        while (end < s.Length && (char.IsDigit((char)s[end]) || s[end] == (byte)'.')) end++;
                        string v = Encoding.ASCII.GetString(s, i, end - i);
                        if (v.Length >= 4) return v;
                    }
                }
            }
            return "";
        }

        private static string ScanComment(LoadedExecutable exe)
        {
            var comment = exe.FindSegment(ElfConsts.PT_SCE_COMMENT);
            if (comment != null && comment.Data.Length > 0)
            {
                int z = Array.IndexOf(comment.Data, (byte)0);
                return z > 0 ? Encoding.ASCII.GetString(comment.Data, 0, z) : "";
            }
            return "";
        }
    }

    public sealed class GameInfo
    {
        public string SourcePath = "";
        public string Kind = "";
        public uint ProcParamMagic;
        public uint SdkVersionRaw;
        public string SdkVersion = "";
        public ulong PoolBase;
        public uint PoolSize;
        public string TitleId = "";
        public string Version = "";
        public string Comment = "";

        public void Print(Logger log)
        {
            log.Info("game", "================ GAME / APP INFO ================");
            log.Info("game", $"Source        : {SourcePath}");
            log.Info("game", $"Kind          : {Kind}");
            log.Info("game", $"Title ID      : {(string.IsNullOrEmpty(TitleId) ? "(not found)" : TitleId)}");
            log.Info("game", $"Version       : {(string.IsNullOrEmpty(Version) ? "(not found)" : Version)}");
            log.Info("game", $"SDK version   : {SdkVersion} (raw 0x{SdkVersionRaw:X8})");
            log.Info("game", $"Pool base     : 0x{PoolBase:X}");
            log.Info("game", $"Pool size     : 0x{PoolSize:X}");
            if (!string.IsNullOrEmpty(Comment))
                log.Info("game", $"Comment       : {Comment}");
            log.Info("game", "===============================================");
        }
    }
}
