using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Linq;

namespace DF60A1
{
    /// <summary>Looks up the SP cost declared by each job's skill script.</summary>
    internal sealed class SkillCatalog
    {
        private static readonly string[] JobListPaths =
        {
            "skill/swordmanskill.lst", "skill/fighterskill.lst", "skill/gunnerskill.lst",
            "skill/mageskill.lst", "skill/priestskill.lst"
        };
        private static readonly Regex ListEntryRegex = new Regex(
            @"(?m)^\s*(\d+)\s+`([^`]+\.skl)`", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex PurchaseCostRegex = new Regex(
            @"(?im)^\s*\[purchase cost\]\s*(\d+)", RegexOptions.Compiled);
        private static readonly Regex TypeRegex = new Regex(
            @"(?im)^\s*\[type\][^\r\n]*\[(active|passive)\]", RegexOptions.Compiled);
        private static readonly Regex FitnessRegex = new Regex(
            @"(?is)\[skill fitness growtype\](?<body>.*?)\[/skill fitness growtype\]", RegexOptions.Compiled);
        private static readonly Regex GrowTypeMaximumLevelRegex = new Regex(
            @"(?im)^\s*\[growtype maximum level\](?<body>[^\r\n]*)", RegexOptions.Compiled);

        private readonly PvfArchiveReader _archive;
        private readonly Dictionary<int, string> _paths = new Dictionary<int, string>();
        private readonly Dictionary<int, int> _costs = new Dictionary<int, int>();
        private readonly Dictionary<int, SkillDefinition> _definitions = new Dictionary<int, SkillDefinition>();
        private readonly Dictionary<byte, List<byte>> _orderedIds = new Dictionary<byte, List<byte>>();
        private readonly object _gate = new object();

        public SkillCatalog(PvfArchiveReader archive)
        {
            _archive = archive;
            if (archive == null) return;
            for (byte job = 0; job < JobListPaths.Length; job++)
            {
                try
                {
                    uint ignored;
                    var text = Decode(archive.ReadAllBytes(JobListPaths[job], out ignored));
                    foreach (Match match in ListEntryRegex.Matches(text))
                    {
                        byte skillId;
                        if (!byte.TryParse(match.Groups[1].Value, out skillId)) continue;
                        var path = "skill/" + match.Groups[2].Value.Replace('\\', '/').TrimStart('/');
                        _paths[Key(job, skillId)] = path;
                        if (!_orderedIds.ContainsKey(job)) _orderedIds[job] = new List<byte>();
                        if (!_orderedIds[job].Contains(skillId)) _orderedIds[job].Add(skillId);
                        try
                        {
                            uint skillFileNumber;
                            SkillDefinition definition;
                            if (TryParseDefinition(skillId, path, Decode(archive.ReadAllBytes(path, out skillFileNumber)), out definition))
                                _definitions[Key(job, skillId)] = definition;
                        }
                        catch
                        {
                            // A malformed skill script is ignored just like a missing one.
                        }
                    }
                }
                catch
                {
                    // A missing job list must not prevent the server from starting.
                }
            }
        }

        public int Count { get { return _paths.Count; } }

        public int GetPurchaseCost(byte job, byte skillId)
        {
            var key = Key(job, skillId);
            lock (_gate)
            {
                int cached;
                if (_costs.TryGetValue(key, out cached)) return cached;
                string path;
                if (_archive == null || !_paths.TryGetValue(key, out path)) return -1;
                try
                {
                    uint ignored;
                    cached = ParsePurchaseCost(Decode(_archive.ReadAllBytes(path, out ignored)));
                }
                catch { cached = -1; }
                _costs[key] = cached;
                return cached;
            }
        }

        public SkillDefinition Get(byte job, byte skillId)
        {
            SkillDefinition result;
            return _definitions.TryGetValue(Key(job, skillId), out result) ? result : null;
        }

        public byte FindFreeSkillSlot(byte job, byte skillId, IEnumerable<CharacterSkillRecord> skills)
        {
            var definition = Get(job, skillId);
            byte first, last;
            if (definition == null || !GamePackets.TryGetSkillClassRange(definition.SkillClass, out first, out last))
                return byte.MaxValue;

            var occupied = new HashSet<byte>((skills ?? Enumerable.Empty<CharacterSkillRecord>())
                .Where(value => value != null).Select(value => value.Slot));
            for (var slot = (int)first; slot <= last; slot++)
                if (!occupied.Contains((byte)slot)) return (byte)slot;
            return byte.MaxValue;
        }

        public IReadOnlyList<SkillDefinition> GetBirthPassiveSkills(byte job)
        {
            List<byte> ordered;
            if (!_orderedIds.TryGetValue(job, out ordered)) return new SkillDefinition[0];
            return ordered.Select(id => Get(job, id))
                .Where(value => value != null && value.Type == SkillType.Passive &&
                    value.SkillId < 191 &&
                    value.PurchaseCost == 0 && value.RequiredLevel <= 1 &&
                    value.FitnessGrowTypes.Contains(job))
                .ToArray();
        }

        // Compatibility entry point for the skill-growth regression and for
        // callers from the earlier reference-server API.  In this client
        // version the automatic level-1 passives are exactly the birth
        // passives selected by job and grow type in the PVF skill list.
        public IReadOnlyList<SkillDefinition> GetAutomaticPassiveSkills(byte job, byte growType, byte level)
        {
            List<byte> ordered;
            if (!_orderedIds.TryGetValue(job, out ordered)) return new SkillDefinition[0];
            return ordered.Select(id => Get(job, id))
                .Where(value => value != null && value.Type == SkillType.Passive &&
                    value.SkillId < 191 && value.PurchaseCost == 0 &&
                    value.RequiredLevel <= level && value.FitnessGrowTypes.Contains(growType) &&
                    growType < value.GrowTypeMaximumLevels.Count &&
                    value.GrowTypeMaximumLevels[growType] > 0)
                .ToArray();
        }

        public static bool TryParseDefinition(byte skillId, string path, string text, out SkillDefinition definition)
        {
            definition = null;
            if (string.IsNullOrEmpty(text)) return false;
            int purchaseCost, requiredLevel, maximumLevel, skillClass;
            var type = TypeRegex.Match(text);
            if (!type.Success || !TryReadFirstInt(text, "purchase cost", out purchaseCost) ||
                !TryReadFirstInt(text, "skill class", out skillClass)) return false;
            TryReadFirstInt(text, "required level", out requiredLevel);
            TryReadFirstInt(text, "maximum level", out maximumLevel);
            var fitness = FitnessRegex.Match(text);
            var growTypeMaximum = GrowTypeMaximumLevelRegex.Match(text);
            if (!fitness.Success || !growTypeMaximum.Success) return false;
            var growTypes = Regex.Matches(fitness.Groups["body"].Value, @"(?m)^\s*(\d+)\s*$")
                .Cast<Match>().Select(match => byte.Parse(match.Groups[1].Value)).ToArray();
            var growTypeMaximumLevels = Regex.Matches(growTypeMaximum.Groups["body"].Value, @"\d+")
                .Cast<Match>().Select(match => byte.Parse(match.Value)).ToArray();
            if (growTypes.Length == 0 || growTypeMaximumLevels.Length == 0) return false;
            definition = new SkillDefinition(skillId, path, string.Equals(type.Groups[1].Value, "passive", StringComparison.OrdinalIgnoreCase)
                ? SkillType.Passive : SkillType.Active, (byte)skillClass, growTypes, growTypeMaximumLevels,
                purchaseCost, requiredLevel, maximumLevel);
            return true;
        }

        internal static int ParsePurchaseCost(string text)
        {
            var match = PurchaseCostRegex.Match(text ?? string.Empty);
            int value;
            return match.Success && int.TryParse(match.Groups[1].Value, out value) && value >= 0 ? value : -1;
        }

        private static bool TryReadFirstInt(string text, string section, out int value)
        {
            value = 0;
            var match = Regex.Match(text ?? string.Empty,
                @"(?im)^\s*\[" + Regex.Escape(section) + @"\][^\r\n]*(?:\r?\n[^\r\n]*)?");
            if (!match.Success) return false;
            var number = Regex.Match(match.Value, @"\d+");
            return number.Success && int.TryParse(number.Value, out value);
        }

        private static int Key(byte job, byte skillId) { return job << 8 | skillId; }
        private static string Decode(byte[] bytes)
        {
            return Encoding.GetEncoding(949).GetString(bytes ?? new byte[0]).Replace("\0", string.Empty);
        }
    }
}
