using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace DF60A1
{
    /// <summary>Lazy lookup for per-item durability from the matching Arad PVF.</summary>
    internal sealed class EquipmentCatalog
    {
        private static readonly Regex ListEntryRegex = new Regex(
            @"(?m)^\s*(\d+)\s+`([^`]+\.equ)`", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex DurabilityRegex = new Regex(
            @"(?im)^\s*\[durability\]\s*(\d+)", RegexOptions.Compiled);
        private static readonly Regex AvatarSelectAbilityRegex = new Regex(
            @"(?is)\[avatar select ability\]\s*(.*?)\s*\[/avatar select ability\]", RegexOptions.Compiled);
        private readonly PvfArchiveReader _archive;
        private readonly Dictionary<ushort, string> _paths = new Dictionary<ushort, string>();
        private readonly Dictionary<ushort, ushort> _durability = new Dictionary<ushort, ushort>();
        private readonly Dictionary<ushort, int> _avatarOptionCounts = new Dictionary<ushort, int>();
        private readonly object _gate = new object();

        public EquipmentCatalog(PvfArchiveReader archive)
        {
            _archive = archive;
            if (archive == null) return;
            uint ignored;
            var text = Decode(archive.ReadAllBytes("equipment/equipment.lst", out ignored));
            foreach (var pair in ParseListEntries(text)) _paths[pair.Key] = pair.Value;
        }

        public int Count { get { return _paths.Count; } }

        public ushort GetMaximumDurability(ushort itemId)
        {
            lock (_gate)
            {
                ushort cached;
                if (_durability.TryGetValue(itemId, out cached)) return cached;
                string path;
                if (_archive == null || !_paths.TryGetValue(itemId, out path)) return 0;
                try
                {
                    uint ignored;
                    cached = ParseMaximumDurability(Decode(_archive.ReadAllBytes(path, out ignored)));
                }
                catch
                {
                    cached = 0;
                }
                _durability[itemId] = cached;
                return cached;
            }
        }

        public bool IsAvatar(ushort itemId)
        {
            string path;
            return _paths.TryGetValue(itemId, out path) &&
                path.IndexOf("/avatar/", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public byte GetAvatarSlot(ushort itemId)
        {
            string path;
            if (!_paths.TryGetValue(itemId, out path)) return byte.MaxValue;
            var normalized = path.Replace('\\', '/').ToLowerInvariant();
            var marker = normalized.IndexOf("/avatar/", StringComparison.Ordinal);
            if (marker < 0) return byte.MaxValue;
            var partStart = marker + 8;
            var partEnd = normalized.IndexOf('/', partStart);
            var part = partEnd < 0 ? normalized.Substring(partStart) : normalized.Substring(partStart, partEnd - partStart);
            switch (part)
            {
                case "cap": return 0;
                case "hair": return 1;
                case "face": return 2;
                // Arad avatar equip slots are not the visual grid order:
                // coat/pants/shoes precede neck/belt on the wire.
                case "coat": return 3;
                case "pants": return 4;
                case "shoes": return 5;
                case "neck": return 6;
                case "belt": return 7;
                case "skin": return 8;
                default: return byte.MaxValue;
            }
        }

        public int GetAvatarOptionCount(ushort itemId)
        {
            lock (_gate)
            {
                int cached;
                if (_avatarOptionCounts.TryGetValue(itemId, out cached)) return cached;
                string path;
                if (_archive == null || !_paths.TryGetValue(itemId, out path)) return 0;
                try
                {
                    uint ignored;
                    cached = ParseAvatarOptionCount(Decode(_archive.ReadAllBytes(path, out ignored)));
                }
                catch
                {
                    cached = 0;
                }
                _avatarOptionCounts[itemId] = cached;
                return cached;
            }
        }

        internal static Dictionary<ushort, string> ParseListEntries(string text)
        {
            var result = new Dictionary<ushort, string>();
            foreach (Match match in ListEntryRegex.Matches(text ?? string.Empty))
            {
                ushort id;
                if (ushort.TryParse(match.Groups[1].Value, out id))
                    result[id] = "equipment/" + match.Groups[2].Value.Replace('\\', '/').TrimStart('/');
            }
            return result;
        }

        internal static ushort ParseMaximumDurability(string text)
        {
            var match = DurabilityRegex.Match(text ?? string.Empty);
            int parsed;
            return match.Success && int.TryParse(match.Groups[1].Value, out parsed)
                ? (ushort)Math.Max(0, Math.Min(ushort.MaxValue, parsed)) : (ushort)0;
        }

        internal static int ParseAvatarOptionCount(string text)
        {
            var match = AvatarSelectAbilityRegex.Match(text ?? string.Empty);
            if (!match.Success) return 0;
            var count = 0;
            foreach (var raw in match.Groups[1].Value.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var line = raw.Split(new[] { "//" }, StringSplitOptions.None)[0].Trim();
                if (line.StartsWith("`[", StringComparison.Ordinal)) count++;
            }
            return count;
        }

        private static string Decode(byte[] bytes)
        {
            return Encoding.GetEncoding(949).GetString(bytes ?? new byte[0]).Replace("\0", string.Empty);
        }
    }
}
