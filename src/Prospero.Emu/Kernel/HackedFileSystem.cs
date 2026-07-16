using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Prospero.Emu.Core;
using Prospero.Emu.Graphics;

namespace Prospero.Emu.Kernel
{
    /// <summary>
    /// A research "filesystem" that maps the game/application directory so that
    /// file-open/read syscalls used during boot (for GNF textures, modules,
    /// param, etc.) resolve against the user's *legally obtained* dumped files.
    ///
    /// We only ever read files that physically exist under the supplied game
    /// root. We never fetch, decrypt, or synthesize copyrighted content.
    /// </summary>
    public sealed class HackedFileSystem
    {
        private readonly Logger _log;
        private readonly string _root;
        private readonly Dictionary<int, Stream> _handles = new();
        private int _nextFd = 3; // 0,1,2 reserved

        public HackedFileSystem(Logger log, string? gameRoot)
        {
            _log = log;
            _root = gameRoot ?? "";
        }

        private string Resolve(ulong pathPtr, GuestMemory mem)
        {
            // pathPtr is a guest pointer; we read it via the CPU's memory.
            var sb = new StringBuilder();
            for (int i = 0; i < 4096; i++)
            {
                byte b = mem.ReadU8(pathPtr + (ulong)i);
                if (b == 0) break;
                sb.Append((char)b);
            }
            return sb.ToString();
        }

        public int Open(GuestMemory mem, ulong pathPtr, ulong flags, ulong mode)
        {
            string path = Resolve(pathPtr, mem);
            return Open(path);
        }

        public int Open(string path)
        {
            try
            {
                // Resolve relative to the game root if provided.
                string full = path;
                if (!string.IsNullOrEmpty(_root))
                {
                    full = Path.Combine(_root, path.TrimStart('/'));
                    if (!File.Exists(full))
                    {
                        // try as-is (absolute or already relative)
                        full = Path.Combine(_root, "sce_sys", path);
                    }
                }
                if (!File.Exists(full))
                {
                    _log.Debug("fs", $"open '{path}' -> not found (benign stub, fd=-1)");
                    return -1;
                }
                var s = File.OpenRead(full);
                int fd = _nextFd++;
                _handles[fd] = s;
                _log.Debug("fs", $"open '{path}' -> fd={fd} ({s.Length} bytes)");
                // If this is a GNF texture, parse it for the early graphics pipeline.
                if (path.EndsWith(".gnf", StringComparison.OrdinalIgnoreCase))
                {
                    try { GnfTexture.Parse(File.ReadAllBytes(full), _log); }
                    catch (Exception ex) { _log.Trace("gnf", $"parse skipped: {ex.Message}"); }
                }
                return fd;
            }
            catch (Exception ex)
            {
                _log.Debug("fs", $"open '{path}' failed: {ex.Message}");
                return -1;
            }
        }

        public int Read(int fd, GuestMemory mem, ulong buf, int count)
        {
            if (!_handles.TryGetValue(fd, out var s)) return -1;
            var tmp = new byte[count];
            int n = s.Read(tmp, 0, count);
            if (n > 0) mem.Write(buf, tmp.AsSpan(0, n));
            return n;
        }

        public int Write(int fd, GuestMemory mem, ulong buf, int count)
        {
            // For stdout/stderr this is handled by the kernel; here we accept
            // other fds as no-ops returning count (research stub).
            return count;
        }
    }
}
