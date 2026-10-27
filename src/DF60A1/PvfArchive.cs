using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DF60A1
{
    internal sealed class PvfArchiveReader
    {
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("DNF_SCRIPT_PACK");
        private readonly byte[] _archive;
        private int _bodyOffset;
        private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        public PvfArchiveReader(string path)
        {
            _archive = File.ReadAllBytes(path);
            if (_archive.Length >= Magic.Length + 12 && Matches(_archive, Magic))
            {
                LoadStandardFormat();
            }
            else if (LooksLikeGuidFormat(_archive))
            {
                LoadGuidFormat();
            }
            else
            {
                throw new InvalidDataException("不是 DNF_SCRIPT_PACK: " + path);
            }
        }

        private void LoadStandardFormat()
        {
            var treeLength = (int)LittleEndian.ReadUInt32(_archive, Magic.Length);
            var treeCrc = LittleEndian.ReadUInt32(_archive, Magic.Length + 4);
            var fileCount = LittleEndian.ReadUInt32(_archive, Magic.Length + 8);
            if (treeLength <= 0 || treeLength % 4 != 0 || treeLength > _archive.Length - Magic.Length - 12) throw new InvalidDataException("PVF 树头无效");
            var tree = new byte[treeLength]; Buffer.BlockCopy(_archive, Magic.Length + 12, tree, 0, treeLength); Transform(tree, treeCrc, false);
            var cursor = 0; var encoding = Encoding.GetEncoding(949);
            for (uint index = 0; index < fileCount; index++)
            {
                if (cursor > tree.Length - 20) throw new InvalidDataException("PVF 树提前结束");
                var number = LittleEndian.ReadUInt32(tree, cursor); var offset = LittleEndian.ReadUInt32(tree, cursor + 4);
                var length = (int)LittleEndian.ReadUInt32(tree, cursor + 8); var crc = LittleEndian.ReadUInt32(tree, cursor + 12);
                var pathLength = (int)LittleEndian.ReadUInt32(tree, cursor + 16); cursor += 20;
                if (length < 0 || pathLength <= 0 || pathLength > tree.Length - cursor) throw new InvalidDataException("PVF 条目无效");
                var virtualPath = Normalize(encoding.GetString(tree, cursor, pathLength).TrimEnd('\0')); cursor += pathLength;
                _entries[virtualPath] = new Entry(number, offset, length, crc);
            }
            _bodyOffset = Magic.Length + 12 + treeLength;
        }

        private void LoadGuidFormat()
        {
            // GUID 变体布局：
            //   uint32 guidLength (0x24) + GUID 字符串
            //   uint32 version
            //   uint32 treeLength
            //   uint32 treeCrc
            //   uint32 fileCount
            //   树：ROR(dword ^ treeCrc, 6)，CRC32 种子 = ~fileCount，期望值 = treeCrc
            //   树条目：number(4) + pathLength(4) + path + length(4) + crc(4) + offset(4)
            //   正文起点 = 56 + treeLength，文件内容：ROR(dword ^ crc, 6)
            var treeLength = (int)LittleEndian.ReadUInt32(_archive, 44);
            var treeCrc = LittleEndian.ReadUInt32(_archive, 48);
            var fileCount = LittleEndian.ReadUInt32(_archive, 52);
            if (treeLength <= 0 || treeLength % 4 != 0 || treeLength > _archive.Length - 56) throw new InvalidDataException("PVF 树头无效");
            var tree = new byte[treeLength]; Buffer.BlockCopy(_archive, 56, tree, 0, treeLength); Transform(tree, treeCrc, false);
            if (Crc32.Compute(tree, fileCount) != treeCrc) throw new InvalidDataException("PVF 树校验失败");
            var cursor = 0;
            var encoding = Encoding.GetEncoding(949);
            for (uint index = 0; index < fileCount; index++)
            {
                if (cursor > tree.Length - 20) throw new InvalidDataException("PVF 树提前结束");
                var number = LittleEndian.ReadUInt32(tree, cursor);
                var pathLength = (int)LittleEndian.ReadUInt32(tree, cursor + 4);
                cursor += 8;
                if (pathLength <= 0 || pathLength > tree.Length - cursor - 12) throw new InvalidDataException("PVF 条目无效");
                var virtualPath = Normalize(encoding.GetString(tree, cursor, pathLength).TrimEnd('\0'));
                cursor += pathLength;
                var length = (int)LittleEndian.ReadUInt32(tree, cursor);
                var crc = LittleEndian.ReadUInt32(tree, cursor + 4);
                var offset = LittleEndian.ReadUInt32(tree, cursor + 8);
                cursor += 12;
                _entries[virtualPath] = new Entry(number, offset, length, crc);
            }
            _bodyOffset = 56 + treeLength;
        }

        private static bool LooksLikeGuidFormat(byte[] archive)
        {
            if (archive.Length < 64) return false;
            var guidLength = LittleEndian.ReadUInt32(archive, 0);
            if (guidLength < 8 || guidLength > 260) return false;
            for (var i = 4; i < 4 + (int)guidLength && i < archive.Length; i++)
            {
                var c = archive[i];
                var ok = c >= (byte)'0' && c <= (byte)'9' || c >= (byte)'a' && c <= (byte)'z' || c >= (byte)'A' && c <= (byte)'Z' || c == '-' || c == '{' || c == '}' || c == '_';
                if (!ok) return false;
            }
            return true;
        }

        public byte[] ReadAllBytes(string path, out uint fileNumber)
        {
            Entry entry; if (!_entries.TryGetValue(Normalize(path), out entry)) throw new FileNotFoundException("PVF 内不存在 " + path);
            fileNumber = entry.Number; var padded = (entry.Length + 3) & ~3; var absolute = checked(_bodyOffset + (int)entry.Offset);
            if (absolute < _bodyOffset || absolute > _archive.Length - padded) throw new InvalidDataException("PVF 条目越界");
            var content = new byte[padded]; Buffer.BlockCopy(_archive, absolute, content, 0, padded); Transform(content, entry.Crc, false);
            if (content.Length == entry.Length) return content; var result = new byte[entry.Length]; Buffer.BlockCopy(content, 0, result, 0, result.Length); return result;
        }

        public IEnumerable<string> EnumeratePaths(string prefix = null)
        {
            var normalizedPrefix = string.IsNullOrEmpty(prefix) ? string.Empty : Normalize(prefix);
            foreach (var path in _entries.Keys)
                if (path.StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase))
                    yield return path;
        }

        private static bool Matches(byte[] source, byte[] expected) { for (var i = 0; i < expected.Length; i++) if (source[i] != expected[i]) return false; return true; }
        private static string Normalize(string path) { return path.Replace('\\', '/').TrimStart('/').ToLowerInvariant(); }
        private static void Transform(byte[] bytes, uint key, bool encrypt)
        {
            for (var offset = 0; offset < bytes.Length; offset += 4)
            {
                var value = LittleEndian.ReadUInt32(bytes, offset);
                value = encrypt ? RotateLeft(value, 6) ^ key : RotateRight(value ^ key, 6);
                LittleEndian.WriteUInt32(bytes, offset, value);
            }
        }
        private static uint RotateLeft(uint value, int count) { return value << count | value >> (32 - count); }
        private static uint RotateRight(uint value, int count) { return value >> count | value << (32 - count); }
        private sealed class Entry { public readonly uint Number, Offset, Crc; public readonly int Length; public Entry(uint number, uint offset, int length, uint crc) { Number = number; Offset = offset; Length = length; Crc = crc; } }

        public static byte[] CreateSingleFile(string virtualPath, byte[] content, uint fileNumber)
        {
            var path = Encoding.ASCII.GetBytes(Normalize(virtualPath)); var paddedContent = new byte[(content.Length + 3) & ~3]; Buffer.BlockCopy(content, 0, paddedContent, 0, content.Length);
            var fileCrc = Crc32.Compute(paddedContent, 0); var tree = new byte[(20 + path.Length + 3) & ~3];
            LittleEndian.WriteUInt32(tree, 0, fileNumber); LittleEndian.WriteUInt32(tree, 4, 0); LittleEndian.WriteUInt32(tree, 8, (uint)content.Length);
            LittleEndian.WriteUInt32(tree, 12, fileCrc); LittleEndian.WriteUInt32(tree, 16, (uint)path.Length); Buffer.BlockCopy(path, 0, tree, 20, path.Length);
            var treeCrc = Crc32.Compute(tree, 1); Transform(tree, treeCrc, true); Transform(paddedContent, fileCrc, true);
            var output = new byte[Magic.Length + 12 + tree.Length + paddedContent.Length]; Buffer.BlockCopy(Magic, 0, output, 0, Magic.Length);
            LittleEndian.WriteUInt32(output, Magic.Length, (uint)tree.Length); LittleEndian.WriteUInt32(output, Magic.Length + 4, treeCrc); LittleEndian.WriteUInt32(output, Magic.Length + 8, 1);
            Buffer.BlockCopy(tree, 0, output, Magic.Length + 12, tree.Length); Buffer.BlockCopy(paddedContent, 0, output, Magic.Length + 12 + tree.Length, paddedContent.Length); return output;
        }
    }
}
