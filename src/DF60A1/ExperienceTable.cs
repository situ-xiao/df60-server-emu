using System;

namespace DF60A1
{
    // 角色升级经验阈值，与 Arad 国服 Script.pvf 的
    // character/exptable.tbl 逐项一致（累计值，非单级增量）。
    internal static class ExperienceTable
    {
        // Arad PVF 的等级上限是 60；这里只保留升到 60 级所需的前
        // 59 个累计阈值。旧实现只抄了前 30 项，而且第 29/30 项有误。
        private static readonly int[] CumulativeExp =
        {
            450, 1464, 3510, 6996, 12362, 20080, 30654, 44620, 62546, 85032,
            115070, 151809, 196095, 248816, 310905, 393985, 494179, 614062, 753257, 917214,
            1123038, 1363827, 1643977, 1962296, 2328481, 2740543, 3210056, 3743055,
            4336134, 5004170, 5883255, 6874123, 7987360, 9217890, 10590791, 12098881,
            13771298, 15621583, 17638994, 19859590, 23812958, 28201365, 33059266,
            38378231, 44231049, 50602242, 57576816, 65197027, 73435756, 82398442,
            97208357, 113383862, 131015224, 150072973, 170753230, 193007209,
            217061521, 243023296, 270812393
        };

        public static int ExpForNextLevel(int nextLevel)
        {
            if (nextLevel < 1) return CumulativeExp[0];
            var index = nextLevel - 1;
            return index < CumulativeExp.Length ? CumulativeExp[index] : 0;
        }

        public static int ExpRequiredWithinLevel(int level)
        {
            var nextThreshold = ExpForNextLevel(level);
            if (nextThreshold <= 0) return 0;
            var currentThreshold = level > 1 ? ExpForNextLevel(level - 1) : 0;
            return Math.Max(0, nextThreshold - currentThreshold);
        }

        public const int SkillPointsPerLevel = 30;

        // Arad 版 SP 跟随当前等级的经验进度获得：每级合计 30 SP，
        // 每跨过 1/30 的升级经验阈值立即增加 1 SP，升级本身不额外增加。
        public static int SkillPointProgress(int level, int experienceWithinLevel)
        {
            var required = ExpRequiredWithinLevel(level);
            if (required <= 0 || experienceWithinLevel <= 0) return 0;
            if (experienceWithinLevel >= required) return SkillPointsPerLevel;
            return (int)((long)experienceWithinLevel * SkillPointsPerLevel / required);
        }

        public static int ApplyExperience(ref int level, ref int experienceWithinLevel, int amount, out bool leveled)
        {
            leveled = false;
            var remaining = Math.Max(0L, amount);
            var gainedSkillPoints = 0;
            experienceWithinLevel = Math.Max(0, experienceWithinLevel);

            while (true)
            {
                var required = ExpRequiredWithinLevel(level);
                if (required <= 0)
                {
                    experienceWithinLevel = (int)Math.Min(int.MaxValue,
                        (long)experienceWithinLevel + remaining);
                    return gainedSkillPoints;
                }

                var before = SkillPointProgress(level, Math.Min(experienceWithinLevel, required));
                var available = (long)experienceWithinLevel + remaining;
                if (available < required)
                {
                    experienceWithinLevel = (int)available;
                    gainedSkillPoints += SkillPointProgress(level, experienceWithinLevel) - before;
                    return gainedSkillPoints;
                }

                gainedSkillPoints += SkillPointsPerLevel - before;
                remaining = available - required;
                experienceWithinLevel = 0;
                level++;
                leveled = true;
                if (remaining == 0) return gainedSkillPoints;
            }
        }

        // Arad 的怪物基础经验由怪物等级决定，不能用玩家当级
        // 升级需求反推。低级段基准为 Lv1=30，之后每级 +10。
        // 同期实测等级差表的两端是“很少”，而非 0；保留 5% 下限。
        // Script.pvf 竞技场脚本明确的普通/精英/超级精英/Boss 倍率为 1/2/3/4。
        public static int MonsterExperience(int characterLevel, int monsterLevel, bool isBoss)
        {
            var normalizedMonsterLevel = Math.Max(1, monsterLevel);
            var baseExp = checked(normalizedMonsterLevel * 10 + 20);
            var difference = characterLevel - normalizedMonsterLevel;
            int percent;
            switch (difference)
            {
                case -6: percent = 70; break;
                case -5: percent = 120; break;
                case -4: percent = 112; break;
                case -3: percent = 107; break;
                case -2: percent = 104; break;
                case -1: percent = 101; break;
                case 0: percent = 100; break;
                case 1: percent = 98; break;
                case 2: percent = 95; break;
                case 3: percent = 88; break;
                case 4: percent = 79; break;
                case 5: percent = 50; break;
                default: percent = 5; break;
            }
            var result = Math.Max(1, baseExp * percent / 100);
            return isBoss ? checked(result * 4) : result;
        }

        // 副本通关额外经验。以角色当前等级的升级需求为基数，
        // 套用副本等级差衰减、难度加成与 Arad 结算评级码加成。
        // 这部分与逐只怪物经验分开，只在 Boss 房通关后发放一次。
        public static int DungeonClearExperience(int characterLevel, int dungeonLevel,
            int difficulty, byte resultCode)
        {
            var levelExp = ExpRequiredWithinLevel(characterLevel);
            if (levelExp <= 0) return 0;
            var baseExp = Math.Max(1, levelExp / 20);
            var levelDifference = Math.Abs(characterLevel - Math.Max(1, dungeonLevel));
            var levelPercent = levelDifference <= 3 ? 100 : levelDifference <= 6 ? 60 : 20;
            var difficultyPercent = 100 + Math.Min(3, Math.Max(0, difficulty)) * 15;
            var rankPercent = resultCode >= 95 ? 130 : resultCode >= 70 ? 120 :
                resultCode >= 55 ? 110 : resultCode >= 35 ? 100 : 90;
            var result = (long)baseExp * levelPercent * difficultyPercent * rankPercent / 1000000L;
            return (int)Math.Max(1, Math.Min(int.MaxValue, result));
        }
    }
}
