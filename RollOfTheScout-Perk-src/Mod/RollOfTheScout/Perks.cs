using System;
using System.Collections.Generic;
using Photon.Pun;
using UnityEngine;

namespace RollOfTheScout
{
    /// <summary>
    /// 词条系统（第二批玩法改造）：把职业效果拆成可独立组合的「词条」，
    /// 每一关为每名玩家抽取 4~5 条（配置可调），而不是固定分配一个职业。
    ///
    /// 与职业系统的关系由配置项「词条-模式」决定：
    /// · 替换（默认）—— 不再分配职业，所有效果都来自词条；
    /// · 叠加         —— 保留职业分配，在其上每关额外抽词条。
    ///
    /// 这里是 partial 的一半：HostClassManager 的另一半在 HostClassManager.cs 里，
    /// 因此词条可以直接调用其中的私有辅助方法（GrantSprint / ForceBackflip / IsDead …）。
    /// </summary>
    public sealed partial class HostClassManager
    {
        // ===============================================================
        // 词条框架
        // ===============================================================

        internal enum PerkCategory
        {
            Normal = 0,   // 从职业效果拆出来的普通词条
            Creepy = 1,   // 怪谈专属词条（默认与普通词条同权重，可在配置里改）
        }

        /// <summary>一条词条。无状态单例：每名玩家的计时由基类的 <see cref="Due"/> 按玩家标识分开存。</summary>
        internal abstract class Perk
        {
            private readonly Dictionary<string, float> _timers = new Dictionary<string, float>();

            /// <summary>稳定标识（配置键用，勿随意改）。</summary>
            public abstract string Id { get; }
            public abstract string NameZh { get; }
            public abstract string DescZh { get; }
            public virtual string NameEn => NameZh;
            public virtual string DescEn => DescZh;
            public virtual PerkCategory Category => PerkCategory.Normal;
            public virtual float Weight => 1f;
            public virtual bool DefaultEnabled => true;

            /// <summary>互斥组：同一组内只会抽到一条（如「深度冻伤 / 外皮烧伤」）。</summary>
            public virtual string ExcludeGroup => null;

            /// <summary>被抽中时调用一次（发放初始物品 / 初始状态等）。</summary>
            public virtual void OnDraw(Character character, string key) { }

            /// <summary>每帧调用（仅对该玩家生效）。</summary>
            public virtual void Tick(Character character, string key, float dt) { }

            /// <summary>被移除时调用（换关 / 转职 / 词条模式切换）。</summary>
            public virtual void OnRemove(Character character, string key) => Forget(key);

            /// <summary>任意玩家死亡时调用（<paramref name="owner"/> = 持有本词条的角色，<paramref name="dead"/> = 死者）。</summary>
            public virtual void OnPlayerDeath(Character owner, Character dead) { }

            /// <summary>累加计时：达到 <paramref name="interval"/> 秒时归零并返回 true。</summary>
            protected bool Due(string key, float dt, float interval)
            {
                if (interval <= 0f) return false;
                _timers.TryGetValue(key, out float elapsed);
                elapsed += dt;
                if (elapsed >= interval)
                {
                    _timers[key] = 0f;
                    return true;
                }
                _timers[key] = elapsed;
                return false;
            }

            protected bool Once(string key, out float elapsed)
            {
                _timers.TryGetValue(key, out elapsed);
                return elapsed > 0f;
            }

            protected void MarkOnce(string key) => _timers[key] = 1f;

            /// <summary>「秒杀类」词条的递进计数：返回本次是第几次触犯（1 起算）。</summary>
            protected int BumpKillCount(string key)
            {
                int count = (int)State(key + "|kill") + 1;
                SetState(key + "|kill", count);
                return count;
            }

            /// <summary>
            /// 秒杀类通用递进惩罚：第 1 次 +10 损伤、第 2 次 +20 损伤、第 3 次起秒杀。
            /// 返回 true 表示本次已致死。
            /// </summary>
            protected bool EscalatingPunish(Character character, string key, string reason)
            {
                int times = BumpKillCount(key);
                if (times <= 2)
                {
                    float injury = times == 1 ? 0.10f : 0.20f;
                    HostApi.ApplyStatus(character, CharacterAfflictions.STATUSTYPE.Injury, injury);
                    HostClassManager.Instance.ShowToast(
                        $"【{reason}】{Nick(character)} 第 {times} 次触犯：损伤 +{injury * 100f:0.#}");
                    Plugin.Log.LogInfo(
                        $"[RollOfTheScout] {reason}：{Nick(character)} 第 {times} 次触犯，" +
                        $"损伤 +{injury * 100f:0.#}（第 3 次起秒杀）。");
                    return false;
                }

                HostClassManager.Instance.KillCharacter(character, $"词条·{reason}");
                return true;
            }

            /// <summary>读取一个附加状态值（用于「上次状态快照」这类无需计时的判定）。</summary>
            protected float State(string key, float fallback = 0f) => _timers.TryGetValue(key + "|s", out float v) ? v : fallback;

            protected void SetState(string key, float value) => _timers[key + "|s"] = value;

            protected void Forget(string key)
            {
                _timers.Remove(key);
                _timers.Remove(key + "|once");
            }
        }

        // ===============================================================
        // 词条目录
        // ===============================================================

        internal static readonly Perk[] PerkCatalog =
        {
            // ---- 普通词条（从职业效果拆出，均可独立生效）----
            new PerkSupplyDrop(),
            new PerkSprintBody(),
            new PerkInfiniteStamina(),
            new PerkHighMetabolism(),
            new PerkSilenceStamina(),
            new PerkRopeHangInjury(),
            new PerkPurify(),
            new PerkDrowsyCurse(),
            new PerkHungryGhost(),
            new PerkScavenger(),

            // ---- 怪谈专属词条 ----
            new PerkBackflipSleep(),
            new PerkEmoteDeath(),

            // ---- 第二批：状态惩罚类（互斥组见各自的 ExcludeGroup）----
            new PerkDeepFrostbite(),
            new PerkSkinBurn(),
            new PerkFleshPiercing(),
            new PerkStiffLimb(),
            new PerkFakeDeath(),
            new PerkJealousy(),
            new PerkOverdependence(),
            new PerkOvereating(),
            new PerkExhaustion(),
            new PerkFragileEyes(),
            new PerkNightBlindness(),
            new PerkGlareSensitivity(),
            new PerkHyperactive(),
            new PerkAlbinism(),
            new PerkBackflipStrain(),
            new PerkDaihaoBlessing(),

            // ---- 第三批：从各职业拆出的普通词条（定时 / 状态 / 进食 / 初始）----
            new PerkInitialPetrify(),
            new PerkInitialCurse(),
            new PerkSoloBleed(),
            new PerkCompanyDrowsy(),
            new PerkEatInjury(),
            new PerkNonBerryDrowsy(),
            new PerkNightSprint(),
            new PerkSoloCleanse(),
            new PerkBugCycle(),
            new PerkEggCycle(),

            // ---- 第四批：团队半径 / 发放物品类 ----
            new PerkBackpackStart(),
            new PerkNursing(),
            new PerkDetox(),
            new PerkRally(),
            new PerkNaturalTenacity(),
            new PerkBananaCycle(),
            new PerkPerfectionism(),
            new PerkFoodSupply(),
            new PerkCannonCycle(),
            new PerkManyQu(),
            new PerkWarmShoulder(),

            // ---- 第五批：剩余定时 / 数值类 ----
            new PerkSoothing(),
            new PerkPeerInjuryReduce(),
            new PerkTeaching(),
            new PerkEnlighten(),
            new PerkExtreme(),
            new PerkBlowback(),
            new PerkSoloStamina(),
            new PerkNasa(),
            new PerkKingberry(),
            new PerkRopeKit(),

            // ---- 第六批：死亡事件类 ----
            new PerkEmpathy(),
            new PerkMorale(),
            new PerkUndead(),

            // ---- 第七批：物品发放类（从职业拆出）----
            new PerkBoneBook(),
            new PerkScrollCycle(),
            new PerkGoldenHook(),
            new PerkPortableStove(),
            new PerkBackpackAll(),

            // ---- 第八批：表情驱动类 ----
            new PerkDanceYouth(),
            new PerkCrowdCheer(),

            // ---- 第九批：周期净化类（参数化，免去重复代码）----
            new PerkDrain("curseDrain", "诅咒递减", "每 60 秒 −2.5 诅咒。",
                "−2.5 curse every 60 s.", CharacterAfflictions.STATUSTYPE.Curse, -0.025f, 60f),
            new PerkDrain("coldResist", "耐寒", "每 10 秒 −2.5 寒冷。",
                "−2.5 Cold every 10 s.", CharacterAfflictions.STATUSTYPE.Cold, -0.025f, 10f),
            new PerkDrain("heatResist", "耐热", "每 10 秒 −2.5 炎热。",
                "−2.5 Hot every 10 s.", CharacterAfflictions.STATUSTYPE.Hot, -0.025f, 10f),
            new PerkDrain("soberUp", "醒神", "每 15 秒 −5 困倦。",
                "−5 drowsiness every 15 s.", CharacterAfflictions.STATUSTYPE.Drowsy, -0.05f, 15f),
            new PerkDrain("sateHunger", "充饥", "每 30 秒 −2.5 饥饿。",
                "−2.5 hunger every 30 s.", CharacterAfflictions.STATUSTYPE.Hunger, -0.025f, 30f),
            new PerkDrain("poisonResist", "强健", "每 20 秒 −2.5 中毒。",
                "−2.5 poison every 20 s.", CharacterAfflictions.STATUSTYPE.Poison, -0.025f, 20f),

            // ---- 第十一批：条件型词条（有触发条件 / 可规避）----
            new PerkMedKit(),
            new PerkFallFromGrace(),
            new PerkStoveHeat(),

            // ---- 第十二批：拾取层 ----
            new PerkAssassinPick(),
            new PerkPharaohCurse(),
            new PerkFastingPlus(),
            new PerkFastingMinus(),
        };

        /// <summary>玩家稳定标识（Perk 用）：直接取 PhotonView.ViewID，与职业账本无关（替换模式下账本为空）。</summary>
        private static string PerkKeyOf(Character character)
        {
            if (character == null || character.photonView == null) return null;
            return character.photonView.ViewID.ToString();
        }

        /// <summary>本局每名玩家抽到的词条。键 = <see cref="PerkKeyOf"/>。</summary>
        private readonly Dictionary<string, List<Perk>> _perks = new Dictionary<string, List<Perk>>();

        /// <summary>抽词条失败的玩家（避免每帧重试与刷日志）。</summary>
        private readonly HashSet<string> _perkFailed = new HashSet<string>();

        /// <summary>下一次「词条诊断」日志时刻。</summary>
        private float _perkDiagAt;

        /// <summary>「词条-模式」解析缓存（该判断每帧都会走到，避免每帧 <c>ToLowerInvariant</c> 分配）。</summary>
        private static string _perkModeRaw;
        private static bool _perkModeReplace;

        /// <summary>「词条-模式」是否为「替换职业」。默认替换；填「叠加」则保留职业。</summary>
        internal static bool PerkModeReplacesClass()
        {
            if (!ModConfig.Perk.Enabled.Value) return false;

            string mode = ModConfig.Perk.Mode.Value ?? string.Empty;
            if (mode != _perkModeRaw)
            {
                _perkModeRaw = mode;
                string lower = mode.ToLowerInvariant();
                _perkModeReplace = !(mode.Contains("叠加") || lower.Contains("overlay"));
            }
            return _perkModeReplace;
        }

        /// <summary>按配置过滤出本局可用的词条池（含怪谈开关与单条开关）。</summary>
        private static List<Perk> BuildPerkPool()
        {
            var pool = new List<Perk>();
            bool creepyOn = ModConfig.Perk.CreepyEnabled.Value;

            foreach (var perk in PerkCatalog)
            {
                if (perk == null) continue;
                if (perk.Category == PerkCategory.Creepy && !creepyOn) continue;
                if (!ModConfig.Perk.EntryEnabled(perk.Id, perk.DefaultEnabled)) continue;
                if (perk.Weight <= 0f) continue;
                pool.Add(perk);
            }
            return pool;
        }

        /// <summary>词条在抽取时的实际权重（怪谈按「怪谈-权重」缩放，默认 1 = 与普通词条相同）。</summary>
        private static float PerkWeightOf(Perk perk)
        {
            float weight = perk.Weight;
            if (IsCreepyPerk(perk))
                weight *= Mathf.Max(0f, ModConfig.Perk.CreepyWeight.Value);
            return weight;
        }

        /// <summary>
        /// 怪谈词条 id（第二批判定为怪谈的那 17 条 + 原有的翻跟头的人 / 别做表情）。
        /// 单独列集合而不是逐个改类的 Category，便于以后调整归属。
        /// </summary>
        private static readonly HashSet<string> CreepyPerkIds = new HashSet<string>
        {
            "backflipSleep", "emoteDeath", "pharaohCurse",
            "deepFrostbite", "skinBurn", "fleshPiercing", "stiffLimb", "fakeDeath",
            "jealousy", "overdependence", "overeating", "exhaustion", "fragileEyes",
            "nightBlindness", "glareSensitivity", "hyperactive", "albinism",
            "backflipStrain", "daihaoBlessing",
        };

        /// <summary>负面词条 id（其余普通词条一律算正面）。</summary>
        private static readonly HashSet<string> NegativePerkIds = new HashSet<string>
        {
            "highMetabolism", "drowsyCurse", "hungryGhost",
            "soloBleed", "companyDrowsy", "eatInjury", "nonBerryDrowsy",
            "initialCurse", "initialPetrify",
            "stoveHeat", "fastingMinus",
        };

        internal static bool IsCreepyPerk(Perk perk)
            => perk != null && (perk.Category == PerkCategory.Creepy || CreepyPerkIds.Contains(perk.Id));

        internal static bool IsNegativePerk(Perk perk)
            => perk != null && NegativePerkIds.Contains(perk.Id);

        /// <summary>词条的原属职业（面板显示用；未收录的按「通用」处理）。</summary>
        private static readonly Dictionary<string, string> PerkSources = new Dictionary<string, string>
        {
            { "supplyDrop", "通用" }, { "sprintBody", "通用" }, { "infiniteStamina", "通用" },
            { "purify", "通用" }, { "drowsyCurse", "通用" }, { "hungryGhost", "通用" },
            { "scavenger", "通用" }, { "initialCurse", "通用" },
            { "highMetabolism", "先驱" }, { "backpackStart", "先驱" },
            { "silenceStamina", "ATacv" }, { "ropeHangInjury", "ATacv" }, { "ropeKit", "ATacv" },
            { "initialPetrify", "后勤" },
            { "soloBleed", "童军" }, { "peerInjuryReduce", "童军" },
            { "companyDrowsy", "独狼" }, { "soloStamina", "独狼" },
            { "eatInjury", "榔王" }, { "nonBerryDrowsy", "榔王" }, { "kingberry", "榔王" },
            { "nightSprint", "程序员" }, { "soloCleanse", "程序员" }, { "bugCycle", "程序员" },
            { "eggCycle", "待皓" }, { "daihaoBlessing", "待皓" },
            { "nursing", "医生" }, { "detox", "医生" }, { "empathy", "医生" },
            { "rally", "领袖" }, { "soothing", "领袖" }, { "morale", "领袖" },
            { "naturalTenacity", "野人" }, { "bananaCycle", "野人" },
            { "perfectionism", "圣人" }, { "fasting", "圣人" }, { "teaching", "圣人" },
            { "foodSupply", "炊事员" }, { "cannonCycle", "炮手" }, { "blowback", "炮手" },
            { "manyQu", "区" }, { "warmShoulder", "救火队员" },
            { "enlighten", "法师" }, { "extreme", "挑战者" },
            { "nasa", "太空人" }, { "undead", "死灵法师" },
            { "backflipSleep", "舞王" },
            { "backpackAll", "仓鼠" }, { "boneBook", "死灵法师" }, { "scrollCycle", "法师" },
            { "goldenHook", "挑战者" }, { "portableStove", "特级厨师" },
            { "danceYouth", "舞王" }, { "crowdCheer", "舞王" },
            { "curseDrain", "死灵法师" }, { "coldResist", "太空人" }, { "heatResist", "太空人" },
            { "soberUp", "圣人" }, { "sateHunger", "特级厨师" }, { "poisonResist", "野人" },
            { "medKit", "医生" }, { "fallFromGrace", "圣人" }, { "stoveHeat", "特级厨师" },
            { "pharaohCurse", "怪谈" },
            { "fastingPlus", "圣人" }, { "fastingMinus", "圣人" },
            { "deepFrostbite", "怪谈" }, { "skinBurn", "怪谈" }, { "fleshPiercing", "怪谈" },
            { "stiffLimb", "怪谈" }, { "fakeDeath", "怪谈" }, { "jealousy", "怪谈" },
            { "overdependence", "怪谈" }, { "overeating", "怪谈" }, { "exhaustion", "怪谈" },
            { "fragileEyes", "怪谈" }, { "nightBlindness", "怪谈" }, { "glareSensitivity", "怪谈" },
            { "hyperactive", "怪谈" }, { "albinism", "怪谈" }, { "backflipStrain", "怪谈" },
            { "emoteDeath", "怪谈" },
        };

        internal static string SourceOf(Perk perk)
        {
            if (perk == null) return "通用";
            return PerkSources.TryGetValue(perk.Id, out string src) && !string.IsNullOrEmpty(src) ? src : "通用";
        }

        /// <summary>把一批抽出来的词条并入结果：按 Id 去重，并遵守互斥组。</summary>
        private static void AppendDraws(List<Perk> into, List<Perk> pool, int count, bool allowDuplicate)
        {
            if (count <= 0 || pool.Count == 0) return;

            foreach (var perk in DrawPerks(pool, count, allowDuplicate))
            {
                if (!allowDuplicate && ExistsById(into, perk.Id)) continue;

                string group = perk.ExcludeGroup;
                if (!string.IsNullOrEmpty(group) && ExistsByGroup(into, group)) continue;

                into.Add(perk);
            }
        }

        private static bool ExistsById(List<Perk> list, string id)
        {
            foreach (var item in list) if (item.Id == id) return true;
            return false;
        }

        private static bool ExistsByGroup(List<Perk> list, string group)
        {
            foreach (var item in list)
            {
                if (item.ExcludeGroup == group) return true;
            }
            return false;
        }

        /// <summary>
        /// 一套完整的词条抽取（三条线各自抽，互不影响）：
        /// 正面 2~3 条 + 负面 2~3 条 + 怪谈 1 条（数量均可在配置里改）。
        /// </summary>
        private static List<Perk> DrawPerkSet(List<Perk> pool)
        {
            var result = new List<Perk>();

            var positives = new List<Perk>();
            var negatives = new List<Perk>();
            var creepies = new List<Perk>();
            foreach (var perk in pool)
            {
                if (IsCreepyPerk(perk)) creepies.Add(perk);
                else if (IsNegativePerk(perk)) negatives.Add(perk);
                else positives.Add(perk);
            }

            bool allowDup = ModConfig.Perk.AllowDuplicate.Value;

            int posMin = Mathf.Max(0, ModConfig.Perk.PositiveMin.Value);
            int posMax = Mathf.Max(posMin, ModConfig.Perk.PositiveMax.Value);
            int negMin = Mathf.Max(0, ModConfig.Perk.NegativeMin.Value);
            int negMax = Mathf.Max(negMin, ModConfig.Perk.NegativeMax.Value);
            int creepyCount = Mathf.Max(0, ModConfig.Perk.CreepyCount.Value);

            AppendDraws(result, positives, UnityEngine.Random.Range(posMin, posMax + 1), allowDup);
            AppendDraws(result, negatives, UnityEngine.Random.Range(negMin, negMax + 1), allowDup);
            AppendDraws(result, creepies, UnityEngine.Random.Range(creepyCount, creepyCount + 1), allowDup);

            EnforcePerkPairs(result, pool);
            return result;
        }

        /// <summary>按权重抽 <paramref name="count"/> 条（默认不重复）。</summary>
        private static List<Perk> DrawPerks(List<Perk> pool, int count, bool allowDuplicate)
        {
            var result = new List<Perk>();
            if (pool == null || pool.Count == 0 || count <= 0) return result;

            var remaining = new List<Perk>(pool);
            for (int i = 0; i < count && remaining.Count > 0; i++)
            {
                float total = 0f;
                foreach (var perk in remaining) total += PerkWeightOf(perk);
                if (total <= 0f) break;

                float roll = UnityEngine.Random.value * total;
                int picked = remaining.Count - 1;
                for (int j = 0; j < remaining.Count; j++)
                {
                    roll -= PerkWeightOf(remaining[j]);
                    if (roll <= 0f)
                    {
                        picked = j;
                        break;
                    }
                }

                result.Add(remaining[picked]);
                if (!allowDuplicate) remaining.RemoveAt(picked);

                // 互斥组：同一组内只能出现一条（如 深度冻伤 / 外皮烧伤、猜忌 / 过度依赖）
                string group = result[result.Count - 1].ExcludeGroup;
                if (!string.IsNullOrEmpty(group))
                {
                    for (int j = remaining.Count - 1; j >= 0; j--)
                    {
                        if (remaining[j].ExcludeGroup == group) remaining.RemoveAt(j);
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// 每帧幂等：给「本关还没抽过词条」的玩家抽词条。
        /// 换关时 <see cref="_perks"/> 会被清空（见主 Tick 的 _pendingNewLevel 分支），从而重新抽取。
        /// </summary>
        private void EnsurePerkAssignments(List<Character> characters)
        {
            if (!ModConfig.Perk.Enabled.Value) return;
            if (!PerkModeReplacesClass()) return;

            // 诊断：每 10 秒打一次「角色数 / 池大小 / 需抽人数 / 卡在哪」，便于定位为什么没抽出来
            if (Time.time >= _perkDiagAt)
            {
                _perkDiagAt = Time.time + 10f;
                int pending = 0, noView = 0, dead = 0;
                foreach (var c in characters)
                {
                    if (c == null) continue;
                    string k = PerkKeyOf(c);
                    if (string.IsNullOrEmpty(k)) { noView++; continue; }
                    if (_perks.ContainsKey(k) || _perkFailed.Contains(k)) continue;
                    if (IsDead(c)) { dead++; continue; }
                    pending++;
                }
                // 正常情况（待抽=0）不再打印，避免每 10 秒刷屏；只有真卡住时才提示
                if (pending > 0)
                {
                    Plugin.Log.LogWarning(
                        $"[RollOfTheScout] 词条未抽出：角色={characters.Count} 已有={_perks.Count} " +
                        $"待抽={pending} 无ViewID={noView} 死亡跳过={dead} 池={BuildPerkPool().Count} " +
                        $"数量={ModConfig.Perk.MinPerLevel.Value}~{ModConfig.Perk.MaxPerLevel.Value}");
                }
            }

            // 先看有没有人需要抽词条：没有就直接返回，避免每帧都重建词条池（无谓分配）
            bool anyPending = false;
            foreach (var character in characters)
            {
                if (character == null) continue;
                string key = PerkKeyOf(character);
                if (string.IsNullOrEmpty(key)) continue;
                if (_perks.ContainsKey(key) || _perkFailed.Contains(key)) continue;
                anyPending = true;
                break;
            }
            if (!anyPending) return;

            var pool = BuildPerkPool();
            if (pool.Count == 0) return;

            if (ModConfig.Perk.PositiveMax.Value <= 0 && ModConfig.Perk.NegativeMax.Value <= 0
                && ModConfig.Perk.CreepyCount.Value <= 0) return;

            foreach (var character in characters)
            {
                if (character == null) continue;

                string key = PerkKeyOf(character);
                if (string.IsNullOrEmpty(key)) continue;
                if (_perks.ContainsKey(key) || _perkFailed.Contains(key)) continue;
                if (IsDead(character)) continue;      // 死亡 / 昏迷中的玩家等复活后再抽

                int want = 0;
                var drawn = DrawPerkSet(pool);
                if (drawn.Count == 0) continue;

                foreach (var perk in drawn)
                {
                    try { perk.OnDraw(character, key); }
                    catch (Exception e)
                    {
                        Plugin.Log.LogError($"[RollOfTheScout] 词条「{perk.NameZh}」发放时异常：{e.Message}");
                    }
                }

                _perks[key] = drawn;
                string names = string.Join("、", drawn.ConvertAll(p => p.NameZh).ToArray());
                Plugin.Log.LogInfo($"[RollOfTheScout] 词条：{Nick(character)} 本关获得 {drawn.Count} 条 → {names}");
                if (ModConfig.Perk.Notify.Value) ShowToast($"【词条】{Nick(character)}：{names}");
            }
        }

        /// <summary>每帧驱动所有玩家的词条。</summary>
        private void TickPerks(List<Character> characters, float dt)
        {
            if (!ModConfig.Perk.Enabled.Value) return;
            if (_perks.Count == 0) return;

            DispatchPerkDeaths(characters);
            EnforceGoldenHookBinding(characters);

            foreach (var character in characters)
            {
                if (character == null) continue;

                string key = PerkKeyOf(character);
                if (string.IsNullOrEmpty(key)) continue;
                if (!_perks.TryGetValue(key, out var list) || list == null) continue;
                if (IsDead(character)) continue;

                foreach (var perk in list)
                {
                    try { perk.Tick(character, key, dt); }
                    catch (Exception e)
                    {
                        Plugin.Log.LogError($"[RollOfTheScout] 词条「{perk.NameZh}」触发时异常：{e.Message}");
                    }
                }
            }
        }

        /// <summary>玩家稳定标识 → 上一帧的「已死亡」状态（用于识别死亡事件）。</summary>
        private readonly Dictionary<string, bool> _perkDeadStates = new Dictionary<string, bool>();

        /// <summary>
        /// 检测死亡事件（<c>data.dead</c> 由 false → true）并分发给所有玩家的词条。
        /// 用轮询而不是接原版死亡事件：原版 RPCA_Die 的旁路信号各版本不一致，轮询更稳且只在这一帧做一次比较。
        /// </summary>
        /// <summary>下一次「金钩抓绑定」检查时刻。</summary>
        private float _goldenHookCheckAt;

        /// <summary>
        /// 【极限】与黄金抓钩绑定：没有该词条的角色，身上的黄金抓钩会被收回（每 2 秒检查一次）。
        /// 用回收而不是「拾取时拒绝」：词条可能在局中重抽时才消失，必须能把已经在身上的钩子清掉。
        /// </summary>
        private void EnforceGoldenHookBinding(List<Character> characters)
        {
            if (Time.time < _goldenHookCheckAt) return;
            _goldenHookCheckAt = Time.time + 2f;

            for (int i = 0; i < characters.Count; i++)
            {
                var character = characters[i];
                if (character == null) continue;

                if (CountItemInInventory(character, ItemNames.GoldenHook) <= 0) continue;
                if (HasPerk(character, "extreme")) continue;

                int removed = HostApi.RemoveItemsFromInventorySlots(character, ItemNames.GoldenHook);
                if (removed > 0)
                    Plugin.Log.LogInfo($"[RollOfTheScout] 极限：{Nick(character)} 没有该词条，已收回 {removed} 把黄金抓钩。");
            }
        }

        private void DispatchPerkDeaths(List<Character> characters)
        {
            for (int i = 0; i < characters.Count; i++)
            {
                var dead = characters[i];
                if (dead == null) continue;

                string key = PerkKeyOf(dead);
                if (string.IsNullOrEmpty(key)) continue;

                bool isDead = dead.data != null && dead.data.dead;
                if (!_perkDeadStates.TryGetValue(key, out bool wasDead))
                {
                    _perkDeadStates[key] = isDead;   // 首帧只建立基线
                    continue;
                }
                if (isDead == wasDead) continue;

                _perkDeadStates[key] = isDead;
                if (!isDead) continue;   // 只处理「死亡」，复活暂不触发

                for (int j = 0; j < characters.Count; j++)
                {
                    var owner = characters[j];
                    if (owner == null) continue;

                    var list = PerksOf(owner);
                    if (list == null) continue;

                    foreach (var perk in list)
                    {
                        try { perk.OnPlayerDeath(owner, dead); }
                        catch (Exception e)
                        {
                            Plugin.Log.LogError($"[RollOfTheScout] 词条「{perk.NameZh}」死亡事件异常：{e.Message}");
                        }
                    }
                }
            }
        }
        private void ClearPerks(string reason, List<Character> characters = null)
        {
            if (_perks.Count > 0 && characters != null)
            {
                foreach (var character in characters)
                {
                    if (character == null) continue;
                    string key = PerkKeyOf(character);
                    if (string.IsNullOrEmpty(key)) continue;
                    if (!_perks.TryGetValue(key, out var list) || list == null) continue;

                    foreach (var perk in list)
                    {
                        try { perk.OnRemove(character, key); }
                        catch (Exception e)
                        {
                            Plugin.Log.LogError($"[RollOfTheScout] 词条「{perk.NameZh}」移除时异常：{e.Message}");
                        }
                    }
                }
            }

            if (_perks.Count > 0) Plugin.Log.LogInfo($"[RollOfTheScout] 词条已清空（{reason}）。");
            _perks.Clear();
            _perkFailed.Clear();
        }

        /// <summary>玩家当前词条（用于面板显示 / 怪谈判定）。</summary>
        private List<Perk> PerksOf(Character character)
        {
            string key = PerkKeyOf(character);
            if (string.IsNullOrEmpty(key)) return null;
            return _perks.TryGetValue(key, out var list) ? list : null;
        }

        /// <summary>
        /// 重抽某人的词条（词条模式下由「转职表情」触发）：
        /// 先按原有条目逐一 <see cref="Perk.OnRemove"/>，再重新抽同样数量的一组。
        /// </summary>
        internal bool RerollPerks(Character character, string reason)
        {
            if (!ModConfig.Perk.Enabled.Value || character == null) return false;

            string key = PerkKeyOf(character);
            if (string.IsNullOrEmpty(key)) return false;

            // 移除旧词条
            if (_perks.TryGetValue(key, out var old) && old != null)
            {
                foreach (var perk in old)
                {
                    try { perk.OnRemove(character, key); }
                    catch (Exception e)
                    {
                        Plugin.Log.LogError($"[RollOfTheScout] 词条「{perk.NameZh}」移除时异常：{e.Message}");
                    }
                }
                _perks.Remove(key);
            }

            var pool = BuildPerkPool();
            if (pool.Count == 0) return false;

            // 重抽词条 → 清空体力条（主体力 + 额外体力），作为重抽的代价
            try
            {
                if (character.data != null) character.data.currentStamina = 0f;
                character.SetExtraStamina(0f);
                Plugin.Log.LogInfo($"[RollOfTheScout] 词条重抽：{Nick(character)} 体力条已清空。");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[RollOfTheScout] 词条重抽：清空体力条失败（{e.Message}）。");
            }

            // 重抽词条 → 清空全部负面状态（含诅咒与石化），让新词条的初始状态从干净底座开始
            try
            {
                HostApi.ClearAllNegativeStatuses(character);
                Plugin.Log.LogInfo($"[RollOfTheScout] 词条重抽：{Nick(character)} 负面状态已清空。");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[RollOfTheScout] 词条重抽：清空负面状态失败（{e.Message}）。");
            }

            int min = Mathf.Max(0, ModConfig.Perk.PositiveMin.Value);
            var drawn = DrawPerkSet(pool);
            if (drawn.Count == 0) return false;

            foreach (var perk in drawn)
            {
                try { perk.OnDraw(character, key); }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"[RollOfTheScout] 词条「{perk.NameZh}」发放时异常：{e.Message}");
                }
            }

            _perks[key] = drawn;
            _perkFailed.Remove(key);

            string names = string.Join("、", drawn.ConvertAll(p => p.NameZh).ToArray());
            Plugin.Log.LogInfo($"[RollOfTheScout] 词条重抽（{reason}）：{Nick(character)} 获得 {drawn.Count} 条 → {names}");
            ShowToast($"【词条重抽】{Nick(character)}：{names}");
            return true;
        }

        /// <summary>面板用：每名玩家一行「昵称：词条A、词条B…」。</summary>
        private List<string> BuildPerkListLines()
        {
            var lines = new List<string>();
            if (_perks.Count == 0) return lines;

            foreach (var character in HostApi.GetActiveCharacters())
            {
                if (character == null) continue;
                var list = PerksOf(character);
                if (list == null || list.Count == 0) continue;

                // 只显示「来源职业？」，同一职业去重；怪谈词条不显示（隐藏惩罚信息）
                var sources = new List<string>(list.Count);
                foreach (var perk in list)
                {
                    if (IsCreepyPerk(perk)) continue;
                    string label = SourceOf(perk) + "？";
                    if (!sources.Contains(label)) sources.Add(label);
                }
                if (sources.Count == 0) continue;
                lines.Add($"{Nick(character)}：{string.Join("，", sources.ToArray())}");
            }
            return lines;
        }

        /// <summary>立即杀死一名玩家（优先走原版 <c>RPCA_Die</c>，失败则用满损伤兜底倒地）。</summary>
        private void KillCharacter(Character character, string reason)
        {
            if (character == null || character.photonView == null) return;
            try
            {
                character.photonView.RPC("RPCA_Die", RpcTarget.All);
                Plugin.Log.LogInfo($"[RollOfTheScout] {reason}：{Nick(character)} 已死亡。");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[RollOfTheScout] {reason}：RPCA_Die 失败（{e.Message}），改用满损伤。");
                HostApi.ApplyStatus(character, CharacterAfflictions.STATUSTYPE.Injury, 2f);
            }
        }

        // ===============================================================
        // 普通词条
        // ===============================================================

        /// <summary>【周期物资】每 90 秒获得 1 件随机补给（优先放入空物品栏）。</summary>
        private sealed class PerkSupplyDrop : Perk
        {
            private static readonly string[] Items =
            {
                "Bandages", "FirstAidKit", "Energy Drink", "FortifiedMilk",
                "RopeSpool", "ClimbingSpike", "Lantern", "Torch", "TrailMix", "Marshmallow",
            };

            public override string Id => "supplyDrop";
            public override string NameZh => "周期物资";
            public override string DescZh => "每 90 秒获得 1 件随机补给（优先放入空物品栏，满了在脚下生成）。";
            public override string DescEn => "One random supply every 90 s (empty slot first, otherwise spawned at your feet).";

            public override void Tick(Character character, string key, float dt)
            {
                if (!Due(key, dt, 90f)) return;
                string item = Items[UnityEngine.Random.Range(0, Items.Length)];
                if (!HostApi.GiveItemToInventorySlots(character, item, out _))
                    HostApi.SpawnItemAtFeet(character, item);
            }
        }

        /// <summary>【冲刺体质】每 40 秒获得一次加速（约 10 秒）。</summary>
        private sealed class PerkSprintBody : Perk
        {
            public override string Id => "sprintBody";
            public override string NameZh => "冲刺体质";
            public override string DescZh => "每 40 秒获得一次加速（约 10 秒）。";
            public override string DescEn => "A sprint (about 10 s) every 40 s.";

            public override void Tick(Character character, string key, float dt)
            {
                if (!Due(key, dt, 40f)) return;
                HostClassManager.Instance.GrantSprint(character, "词条·冲刺体质");
            }
        }

        /// <summary>【无限体力】每 120 秒获得一次无限体力。</summary>
        private sealed class PerkInfiniteStamina : Perk
        {
            public override string Id => "infiniteStamina";
            public override string NameZh => "无限体力";
            public override string DescZh => "每 120 秒获得一次无限体力。";
            public override string DescEn => "Infinite stamina once every 120 s.";

            public override void Tick(Character character, string key, float dt)
            {
                if (!Due(key, dt, 120f)) return;
                HostClassManager.Instance.GrantInfiniteStamina(character, "词条·无限体力");
            }
        }

        /// <summary>【高代谢】每 20 秒 +2.5 饥饿。</summary>
        private sealed class PerkHighMetabolism : Perk
        {
            public override string Id => "highMetabolism";
            public override string NameZh => "高代谢";
            public override string DescZh => "每 20 秒 +2.5 饥饿（负面）。";
            public override string DescEn => "+2.5 hunger every 20 s (drawback).";

            public override void Tick(Character character, string key, float dt)
            {
                if (!Due(key, dt, 20f)) return;
                HostApi.ApplyStatus(character, CharacterAfflictions.STATUSTYPE.Hunger, 0.025f);
            }
        }

        /// <summary>【沉默回体】每 15 秒检测一次，没在讲话则 +15 额外体力。</summary>
        private sealed class PerkSilenceStamina : Perk
        {
            public override string Id => "silenceStamina";
            public override string NameZh => "沉默回体";
            public override string DescZh => "每 15 秒检测一次，检测瞬间没在讲话则 +15 额外体力。";
            public override string DescEn => "Every 15 s: if you are not speaking at that moment, +15 extra stamina.";

            public override void Tick(Character character, string key, float dt)
            {
                if (!Due(key, dt, 15f)) return;
                if (IsActuallySpeaking(character)) return;
                HostApi.BoostExtraStamina(character, 0.15f);
            }
        }

        /// <summary>【挂绳易伤】连续挂在绳索上超过 3 秒 → 一次性 +100 损伤。</summary>
        private sealed class PerkRopeHangInjury : Perk
        {
            public override string Id => "ropeHangInjury";
            public override string NameZh => "挂绳易伤";
            public override string DescZh => "连续挂在绳索上超过 3 秒时，一次性 +100 损伤（离开绳索计时清零，同一次挂绳只罚一次）。";
            public override string DescEn => "Staying on a rope for more than 3 s costs +100 injury once per hang.";

            public override void Tick(Character character, string key, float dt)
            {
                bool onRope = character.data != null && character.data.isRopeClimbing;
                if (!onRope)
                {
                    Forget(key);
                    return;
                }
                if (Due(key, dt, 3f))
                {
                    HostApi.ApplyStatus(character, CharacterAfflictions.STATUSTYPE.Injury, 1f);
                    HostClassManager.Instance.ShowToast($"【挂绳易伤】{Nick(character)} 在绳索上挂太久了");
                }
            }
        }

        /// <summary>【净化体质】每 30 秒清除 2.5 困倦与 2.5 损伤。</summary>
        private sealed class PerkPurify : Perk
        {
            public override string Id => "purify";
            public override string NameZh => "净化体质";
            public override string DescZh => "每 30 秒清除 2.5 困倦与 2.5 损伤。";
            public override string DescEn => "Every 30 s: −2.5 drowsiness and −2.5 injury.";

            public override void Tick(Character character, string key, float dt)
            {
                if (!Due(key, dt, 30f)) return;
                HostApi.ApplyStatus(character, CharacterAfflictions.STATUSTYPE.Drowsy, -0.025f);
                HostApi.ApplyStatus(character, CharacterAfflictions.STATUSTYPE.Injury, -0.025f);
            }
        }

        /// <summary>【困倦缠身】每 30 秒 +10 困倦。</summary>
        private sealed class PerkDrowsyCurse : Perk
        {
            public override string Id => "drowsyCurse";
            public override string NameZh => "困倦缠身";
            public override string DescZh => "每 30 秒 +10 困倦（负面）。";
            public override string DescEn => "+10 drowsiness every 30 s (drawback).";

            public override void Tick(Character character, string key, float dt)
            {
                if (!Due(key, dt, 30f)) return;
                HostApi.ApplyStatus(character, CharacterAfflictions.STATUSTYPE.Drowsy, 0.10f);
            }
        }

        /// <summary>【饿鬼】每 30 秒 +5 饥饿。</summary>
        private sealed class PerkHungryGhost : Perk
        {
            public override string Id => "hungryGhost";
            public override string NameZh => "饿鬼";
            public override string DescZh => "每 30 秒 +5 饥饿（负面）。";
            public override string DescEn => "+5 hunger every 30 s (drawback).";

            public override void Tick(Character character, string key, float dt)
            {
                if (!Due(key, dt, 30f)) return;
                HostApi.ApplyStatus(character, CharacterAfflictions.STATUSTYPE.Hunger, 0.05f);
            }
        }

        /// <summary>【拾荒癖】每 25 秒减少 1.25 损伤。</summary>
        private sealed class PerkScavenger : Perk
        {
            public override string Id => "scavenger";
            public override string NameZh => "拾荒癖";
            public override string DescZh => "每 25 秒减少 1.25 损伤。";
            public override string DescEn => "−1.25 injury every 25 s.";

            public override void Tick(Character character, string key, float dt)
            {
                if (!Due(key, dt, 25f)) return;
                HostApi.ApplyStatus(character, CharacterAfflictions.STATUSTYPE.Injury, -0.0125f);
            }
        }

        // ===============================================================
        // 怪谈专属词条
        // ===============================================================

        /// <summary>
        /// 【翻跟头的人】怪谈：每 60 秒被强制做一次后空翻（翻完必定摔倒），并叠加大量困倦（「睡眠」）。
        /// 借原版后空翻表情 <c>A_Scout_Emote_BackFlip</c> 实现；<c>succeeded = true</c> 时原版会在翻完调 Fall(1f)。
        /// </summary>
        private sealed class PerkBackflipSleep : Perk
        {
            public override string Id => "backflipSleep";
            public override string NameZh => "翻跟头的人";
            public override string DescZh => "每 60 秒被强制后空翻一次（必定摔倒），并 +60 困倦。";
            public override string DescEn => "Every 60 s you are forced into a backflip (you always fall) and gain +60 drowsiness.";
            public override PerkCategory Category => PerkCategory.Creepy;

            public override void Tick(Character character, string key, float dt)
            {
                if (!Due(key, dt, 60f)) return;
                HostClassManager.Instance.ForceBackflip(character, "词条·翻跟头的人");
                HostApi.ApplyStatus(character, CharacterAfflictions.STATUSTYPE.Drowsy, 0.60f);
            }
        }

        /// <summary>
        /// 【别做表情】怪谈：做出**任何**表情的瞬间立刻死亡（原版 <c>RPCA_Die</c>）。
        /// 判定挂在表情补丁的主入口上（见 <see cref="OnEmotePlayed"/>），因此跳舞 / 后空翻 / 装死都算。
        /// </summary>
        private sealed class PerkEmoteDeath : Perk
        {
            public override string Id => "emoteDeath";
            public override string NameZh => "别做表情";
            public override string DescZh => "做出任何表情的瞬间立刻死亡。";
            public override string DescEn => "Performing any emote kills you instantly.";
            public override PerkCategory Category => PerkCategory.Creepy;

            public override void OnDraw(Character character, string key)
            {
                HostClassManager.Instance.ShowToast($"【怪谈】{Nick(character)} 抽到了「别做表情」——别乱动");
            }

            /// <summary>表情触发：前两次是递增损伤，第三次起秒杀。</summary>
            public bool Trigger(Character character, string key)
                => EscalatingPunish(character, key, "别做表情");
        }

        /// <summary>表情发生时检查怪谈词条【别做表情】；命中即杀并返回 true。</summary>
        private bool TryEmoteDeathPerk(Character dancer, string emoteName)
        {
            if (!ModConfig.Perk.Enabled.Value) return false;

            // ① 后空翻本身不算触犯（它是很多词条的强制动作，不该连带秒杀）
            if (string.Equals(emoteName, PerkEmoteBackflip, StringComparison.OrdinalIgnoreCase)) return false;

            // ② 主机强制播放的表情不算触犯（多动症 / 翻跟头的人 / 肢体僵硬 等）
            if (_suppressReclassEmote > 0) return false;

            var list = PerksOf(dancer);
            if (list == null) return false;

            string key = PerkKeyOf(dancer);
            foreach (var perk in list)
            {
                if (perk is PerkEmoteDeath emoteDeath)
                {
                    // 递进惩罚：第 1 次 +10 损伤、第 2 次 +20 损伤、第 3 次起秒杀
                    return emoteDeath.Trigger(dancer, key);
                }
            }
            return false;
        }

        // ===============================================================
        // 词条公用小工具（第二批起的词条共用）
        // ===============================================================

        internal const string PerkEmoteBackflip = "A_Scout_Emote_BackFlip";
        internal const string PerkEmotePlayDead = "A_Scout_Emote_PlayDead";

        /// <summary>现在是不是夜晚（原版 <c>DayNightManager.isDay</c> 插值 &lt; 0.5）。</summary>
        private static bool PerkIsNight()
        {
            var manager = DayNightManager.instance;
            if (manager == null) return false;
            return manager.isDay < 0.5f;
        }

        private static bool PerkIsDay() => !PerkIsNight();

        /// <summary>
        /// 是否处于阴影中：从角色朝太阳方向打一条射线，被地形 / 物体挡住即为阴影。
        /// 原版没有「在阴影里」的状态字段，只能用几何遮挡判断；射线忽略触发器，长度 200 米足够覆盖关卡尺度。
        /// </summary>
        private static bool PerkInShadow(Character character)
        {
            if (character == null) return false;

            var manager = DayNightManager.instance;
            if (manager == null || manager.sun == null) return false;

            try
            {
                Transform sun = manager.sun.transform;
                Vector3 origin = character.Center + Vector3.up * 0.4f;
                Vector3 toSun = -sun.forward;          // 太阳光的照射方向取反 = 指向太阳
                return Physics.Raycast(origin, toSun, 200f, ~0, QueryTriggerInteraction.Ignore);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>是否正在攀爬（岩壁 / 绳索 / 藤蔓都算）。</summary>
        private static bool PerkIsClimbing(Character character)
        {
            if (character == null || character.data == null) return false;
            return character.data.isClimbing || character.data.isRopeClimbing || character.data.isVineClimbing;
        }

        /// <summary>半径内除自己以外的存活玩家数量。</summary>
        private static int PerkNearbyOthers(Character character, float radius)
        {
            if (character == null) return 0;
            int count = 0;
            foreach (var other in HostApi.GetCharactersInRadius(character.Center, Mathf.Max(0f, radius)))
            {
                if (other == null || other == character) continue;
                if (IsDead(other)) continue;
                count++;
            }
            return count;
        }

        /// <summary>致盲一次（原版 <c>Peak.Afflictions.Affliction_Blind</c>，失败则退回 isBlind 标记）。</summary>
        private void PerkBlind(Character character, string reason)
        {
            if (character == null) return;
            try
            {
                if (character.refs != null && character.refs.afflictions != null)
                {
                    character.refs.afflictions.AddAffliction(new Peak.Afflictions.Affliction_Blind(), true);
                    Plugin.Log.LogInfo($"[RollOfTheScout] {reason}：{Nick(character)} 致盲。");
                    return;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[RollOfTheScout] {reason}：施加致盲异常（{e.Message}），改用 isBlind 标记。");
            }
            if (character.data != null) character.data.isBlind = true;
        }

        /// <summary>「动弹不得」近似实现：+100 困倦（原版会直接把人放倒）。</summary>
        private void PerkParalyze(Character character, string reason)
        {
            if (character == null) return;
            HostApi.ApplyStatus(character, CharacterAfflictions.STATUSTYPE.Drowsy, 1f);
            Plugin.Log.LogInfo($"[RollOfTheScout] {reason}：{Nick(character)} 动弹不得（+100 困倦）。");
        }

        /// <summary>
        /// 坏蘑菇（身上挂 <c>Action_RandomMushroomEffect</c> 的物品）的预制体名。
        /// 名字不写死：首次使用时从 <c>ItemDatabase.Objects</c> 里按组件筛出来并缓存（反射，避免猜名字）。
        /// </summary>
        private static string _badMushroomPrefab;
        private static bool _badMushroomResolved;

        internal static string FindBadMushroomPrefab()
        {
            if (_badMushroomResolved) return _badMushroomPrefab;
            _badMushroomResolved = true;

            const System.Reflection.BindingFlags any =
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;

            try
            {
                object db = null;
                var instanceProp = typeof(ItemDatabase).GetProperty("Instance", any | System.Reflection.BindingFlags.Static);
                if (instanceProp != null) db = instanceProp.GetValue(null);
                if (db == null)
                {
                    var instanceField = typeof(ItemDatabase).GetField("instance", any | System.Reflection.BindingFlags.Static);
                    if (instanceField != null) db = instanceField.GetValue(null);
                }
                if (db == null)
                {
                    Plugin.Log.LogWarning("[RollOfTheScout] 坏蘑菇：取不到 ItemDatabase 实例。");
                    return null;
                }

                var objectsField = typeof(ItemDatabase).GetField("Objects", any | System.Reflection.BindingFlags.Instance);
                if (objectsField == null)
                {
                    Plugin.Log.LogWarning("[RollOfTheScout] 坏蘑菇：ItemDatabase 上没有 Objects 字段。");
                    return null;
                }

                if (!(objectsField.GetValue(db) is System.Collections.IEnumerable items))
                {
                    Plugin.Log.LogWarning("[RollOfTheScout] 坏蘑菇：ItemDatabase.Objects 不可枚举。");
                    return null;
                }

                foreach (var obj in items)
                {
                    if (!(obj is Item item) || item == null) continue;
                    if (item.GetComponent<Action_RandomMushroomEffect>() == null) continue;
                    _badMushroomPrefab = item.gameObject.name;
                    Plugin.Log.LogInfo($"[RollOfTheScout] 坏蘑菇：找到带随机效果的蘑菇「{_badMushroomPrefab}」。");
                    break;
                }

                if (string.IsNullOrEmpty(_badMushroomPrefab))
                    Plugin.Log.LogWarning("[RollOfTheScout] 坏蘑菇：ItemDatabase 里没有带 Action_RandomMushroomEffect 的物品。");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[RollOfTheScout] 坏蘑菇：查找失败：{e.Message}");
            }
            return _badMushroomPrefab;
        }

        /// <summary>
        /// 让该玩家吃一个坏蘑菇 —— 由原版自己跑 <c>Action_RandomMushroomEffect</c> 的随机效果。
        /// 走模组既有的「喂食物品」通道（<see cref="FeedTransientItem"/>：生成到场景外再喂食），
        /// 因此效果与玩家自己吃掉那朵蘑菇完全一致（屁 / 致盲 / 动弹不得 / 隐藏状态栏等都由原版决定）。
        /// 找不到坏蘑菇时退化为一项随机负面状态。
        /// </summary>
        private void PerkBadMushroomEffect(Character character, string reason, int effectIndex)
        {
            if (character == null) return;

            string prefab = FindBadMushroomPrefab();
            if (string.IsNullOrEmpty(prefab))
            {
                switch (UnityEngine.Random.Range(0, 3))
                {
                    case 0: HostApi.ApplyStatus(character, CharacterAfflictions.STATUSTYPE.Poison, 0.10f); break;
                    case 1: HostApi.ApplyStatus(character, CharacterAfflictions.STATUSTYPE.Drowsy, 0.20f); break;
                    default: HostApi.ApplyStatus(character, CharacterAfflictions.STATUSTYPE.Injury, 0.05f); break;
                }
                Plugin.Log.LogWarning($"[RollOfTheScout] {reason}：未找到坏蘑菇，退回随机负面状态。");
                return;
            }

            // effectIndex >= 0 → 用原版 useDebugEffect/debugEffect 固定该效果；< 0 → 原版随机
            FeedTransientItemWithMushroomEffect(character, prefab, reason, effectIndex);
            Plugin.Log.LogInfo($"[RollOfTheScout] {reason}：{Nick(character)} 吃下坏蘑菇「{prefab}」" +
                (effectIndex >= 0 ? $"（强制效果索引 {effectIndex}）" : "（随机效果）"));
        }

        /// <summary>强制某角色做一个表情（走原版 RPCA_PlayRemove）。</summary>
        private void PerkForceEmote(Character character, string emoteName, bool succeeded)
        {
            if (character == null || character.photonView == null) return;
            _suppressReclassEmote++;
            try
            {
                character.photonView.RPC("RPCA_PlayRemove", RpcTarget.All, emoteName, succeeded);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[RollOfTheScout] 词条强制表情失败：{e.Message}");
            }
            finally
            {
                _suppressReclassEmote = Mathf.Max(0, _suppressReclassEmote - 1);
            }
        }

        /// <summary>该角色是否持有某条词条（供补丁侧查询）。</summary>
        internal bool HasPerk(Character character, string perkId)
        {
            var list = PerksOf(character);
            if (list == null) return false;
            foreach (var perk in list)
            {
                if (perk != null && perk.Id == perkId) return true;
            }
            return false;
        }

        /// <summary>
        /// 表情相关词条（【筋骨劳损】后空翻 +10 损伤、【真假死】装死直接死亡）。
        /// 返回 true 表示已致死，调用方应中断后续表情逻辑。
        /// </summary>
        private bool TryPerkEmoteEffects(Character dancer, string emoteName)
        {
            if (!ModConfig.Perk.Enabled.Value || dancer == null || string.IsNullOrEmpty(emoteName)) return false;

            var list = PerksOf(dancer);
            if (list == null) return false;

            bool isBackflip = string.Equals(emoteName, PerkEmoteBackflip, StringComparison.OrdinalIgnoreCase);
            bool isPlayDead = string.Equals(emoteName, PerkEmotePlayDead, StringComparison.OrdinalIgnoreCase);
            bool isDance = IsDanceEmote(emoteName);

            foreach (var perk in list)
            {
                if (isPlayDead && perk is PerkFakeDeath fakeDeath)
                {
                    // 递进惩罚：第 1 次 +10 损伤、第 2 次 +20 损伤、第 3 次起秒杀
                    if (fakeDeath.Trigger(dancer, PerkKeyOf(dancer))) return true;
                    continue;
                }
                if (isBackflip && perk is PerkBackflipStrain)
                {
                    HostApi.ApplyStatus(dancer, CharacterAfflictions.STATUSTYPE.Injury, 0.10f);
                    Plugin.Log.LogInfo($"[RollOfTheScout] 筋骨劳损：{Nick(dancer)} 做了后空翻，损伤 +10。");
                }
                if (isDance && perk is PerkDanceYouth danceYouth)
                {
                    danceYouth.Trigger(dancer);
                }
                if (isDance && perk is PerkCrowdCheer crowdCheer)
                {
                    crowdCheer.Trigger(dancer);
                }
            }
            return false;
        }

        /// <summary>食用物品时的词条判定（【过量饮食】【进食损伤】【非莓困倦】）。由 <see cref="HandleItemConsumed"/> 调用。</summary>
        internal void OnPerkItemConsumed(Item item, Character character)
        {
            if (!ModConfig.Perk.Enabled.Value || character == null) return;

            var list = PerksOf(character);
            if (list == null) return;

            string itemName = null;
            foreach (var perk in list)
            {
                if (perk is PerkOvereating)
                {
                    // 「扣除的饥饿大于你拥有的饥饿」：饥饿低于 25 点还继续进食即视为过量
                    float hunger = HostApi.GetStatus(character, CharacterAfflictions.STATUSTYPE.Hunger);
                    if (hunger < 0.25f)
                        HostClassManager.Instance.PerkBadMushroomEffect(character, "词条·过量饮食",
                            ModConfig.Perk.OvereatingEffectIndex.Value);
                }
                else if (perk is PerkEatInjury)
                {
                    HostApi.ApplyStatus(character, CharacterAfflictions.STATUSTYPE.Injury, 0.05f);
                }
                else if (perk is PerkNonBerryDrowsy)
                {
                    if (itemName == null) itemName = PerkItemNameOf(item);
                    if (!PerkIsBerryName(itemName))
                        HostApi.ApplyStatus(character, CharacterAfflictions.STATUSTYPE.Drowsy, 0.15f);
                }
            }
        }

        /// <summary>给玩家一件物品：优先放入空物品栏，满了在脚下生成。</summary>
        private static void Give(Character character, string prefabName)
        {
            if (character == null || string.IsNullOrEmpty(prefabName)) return;
            if (!HostApi.GiveItemToInventorySlots(character, prefabName, out _))
                HostApi.SpawnItemAtFeet(character, prefabName);
        }

        /// <summary>取物品预制体名（与原版一致：按 itemID 反查预制体）。</summary>
        internal static string PerkItemNameOf(Item item)
        {
            if (item == null) return string.Empty;
            try
            {
                if (ItemDatabase.TryGetItem(item.itemID, out Item prefab) && prefab != null)
                    return prefab.gameObject.name;
            }
            catch { }
            return string.Empty;
        }

        /// <summary>名字里带荔莓 / 香蕉莓之类的都算「莓」。使用荔王的物品命名（Kingberry / Berrynana）。</summary>
        private static bool PerkIsBerryName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            return name.IndexOf("Kingberry", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Berrynana", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Berry", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // ===============================================================
        // 第三批：从各职业拆出的普通词条
        // ===============================================================

        /// <summary>【积损】开局自带高石化（后勤）。</summary>
        private sealed class PerkInitialPetrify : Perk
        {
            public override string Id => "initialPetrify";
            public override string NameZh => "积损";
            public override string DescZh => "开局自带 70 石化。";
            public override string DescEn => "Start the run with 70 petrify.";

            public override void OnDraw(Character character, string key)
                => HostApi.SetPetrify(character, 70);
        }

        /// <summary>【初始诅咒】开局自带 20 诅咒。</summary>
        private sealed class PerkInitialCurse : Perk
        {
            public override string Id => "initialCurse";
            public override string NameZh => "初始诅咒";
            public override string DescZh => "开局自带 20 诅咒。";
            public override string DescEn => "Start the run with 20 curse.";

            public override void OnDraw(Character character, string key)
                => HostApi.ApplyStatus(character, CharacterAfflictions.STATUSTYPE.Curse, 0.20f);
        }


        /// <summary>【独行流血】身边 50 米内无人时每 5 秒 +2.5 损伤（童军）。</summary>
        private sealed class PerkSoloBleed : Perk
        {
            public override string Id => "soloBleed";
            public override string NameZh => "独行流血";
            public override string DescZh => "50 米内没有其他玩家时，每 5 秒 +2.5 损伤。";
            public override string DescEn => "While no other player is within 50 m, +2.5 injury every 5 s.";

            public override void Tick(Character character, string key, float dt)
            {
                if (!Due(key, dt, 5f)) return;
                if (PerkNearbyOthers(character, 50f) > 0) return;
                HostApi.ApplyStatus(character, CharacterAfflictions.STATUSTYPE.Injury, 0.025f);
            }
        }

        /// <summary>【结伴困倦】15 米内有其他玩家时每 3 秒 +2.5 困倦（独狼）。</summary>
        private sealed class PerkCompanyDrowsy : Perk
        {
            public override string Id => "companyDrowsy";
            public override string NameZh => "结伴困倦";
            public override string DescZh => "15 米内有其他玩家时，每 3 秒 +2.5 困倦。";
            public override string DescEn => "While another player is within 15 m, +2.5 drowsiness every 3 s.";

            public override void Tick(Character character, string key, float dt)
            {
                if (!Due(key, dt, 3f)) return;
                if (PerkNearbyOthers(character, 15f) == 0) return;
                HostApi.ApplyStatus(character, CharacterAfflictions.STATUSTYPE.Drowsy, 0.025f);
            }
        }

        /// <summary>【进食损伤】每次进食 +5 损伤（榔王·纤维化）。</summary>
        private sealed class PerkEatInjury : Perk
        {
            public override string Id => "eatInjury";
            public override string NameZh => "进食损伤";
            public override string DescZh => "每次食用物品后 +5 损伤。";
            public override string DescEn => "+5 injury after eating any item.";
        }

        /// <summary>【非莓困倦】食用非莓类食物时 +15 困倦（榔王·上瘾）。</summary>
        private sealed class PerkNonBerryDrowsy : Perk
        {
            public override string Id => "nonBerryDrowsy";
            public override string NameZh => "非莓困倦";
            public override string DescZh => "食用非莓类食物时 +15 困倦。";
            public override string DescEn => "+15 drowsiness when eating food that is not a berry.";
        }

        /// <summary>【夜间加速】夜晚每 60 秒获得一次加速（程序员·熬夜）。</summary>
        private sealed class PerkNightSprint : Perk
        {
            public override string Id => "nightSprint";
            public override string NameZh => "夜间加速";
            public override string DescZh => "夜晚每 60 秒获得一次加速（约 10 秒）。";
            public override string DescEn => "At night, a sprint (about 10 s) every 60 s.";

            public override void Tick(Character character, string key, float dt)
            {
                if (PerkIsDay()) return;
                if (!Due(key, dt, 60f)) return;
                HostClassManager.Instance.GrantSprint(character, "词条·夜间加速");
            }
        }

        /// <summary>【独行清负】30 米内无人时每 10 秒 −2.5 损伤 / −2.5 困倦（程序员·开挂）。</summary>
        private sealed class PerkSoloCleanse : Perk
        {
            public override string Id => "soloCleanse";
            public override string NameZh => "独行清负";
            public override string DescZh => "30 米内没有其他玩家时，每 10 秒 −2.5 损伤与 −2.5 困倦。";
            public override string DescEn => "While no other player is within 30 m, −2.5 injury and −2.5 drowsiness every 10 s.";

            public override void Tick(Character character, string key, float dt)
            {
                if (!Due(key, dt, 10f)) return;
                if (PerkNearbyOthers(character, 30f) > 0) return;
                HostApi.ApplyStatus(character, CharacterAfflictions.STATUSTYPE.Injury, -0.025f);
                HostApi.ApplyStatus(character, CharacterAfflictions.STATUSTYPE.Drowsy, -0.025f);
            }
        }

        /// <summary>【周期昆虫】每 180 秒获得一个随机昆虫并附带中毒（程序员·BUG）。</summary>
        private sealed class PerkBugCycle : Perk
        {
            public override string Id => "bugCycle";
            public override string NameZh => "周期昆虫";
            public override string DescZh => "每 180 秒获得一个随机昆虫（蜱虫 / 蝎子 / 早起虫儿），蜱虫 +10 中毒、蝎子 +25 中毒。";
            public override string DescEn => "Every 180 s: a random insect (tick / scorpion / early worm); tick +10 poison, scorpion +25 poison.";

            public override void Tick(Character character, string key, float dt)
            {
                if (!Due(key, dt, 180f)) return;

                int roll = UnityEngine.Random.Range(0, 100);
                string prefab;
                float poison;
                if (roll < 70)
                {
                    prefab = ModConfig.Programmer.BugTickItem.Value;
                    poison = ModConfig.Programmer.BugTickPoison.Value;
                }
                else if (roll < 90)
                {
                    prefab = ModConfig.Programmer.BugScorpionItem.Value;
                    poison = ModConfig.Programmer.BugScorpionPoison.Value;
                }
                else
                {
                    prefab = ModConfig.Programmer.BugWormItem.Value;
                    poison = 0f;
                }

                if (string.IsNullOrEmpty(prefab)) return;
                if (!HostApi.GiveItemToInventorySlots(character, prefab, out _)) return;
                if (poison != 0f) HostApi.ApplyStatus(character, CharacterAfflictions.STATUSTYPE.Poison, poison);
            }
        }

        /// <summary>【周期产蛋】每 60 秒获得一个蛋（待皓·生产）。</summary>
        private sealed class PerkEggCycle : Perk
        {
            public override string Id => "eggCycle";
            public override string NameZh => "周期产蛋";
            public override string DescZh => "每 60 秒获得一个蛋（优先放入空物品栏）。";
            public override string DescEn => "One egg every 60 s (empty inventory slot first).";

            public override void Tick(Character character, string key, float dt)
            {
                if (!Due(key, dt, 60f)) return;
                if (!HostApi.GiveItemToInventorySlots(character, ItemNames.NestEgg, out _))
                    HostApi.SpawnItemAtFeet(character, ItemNames.NestEgg);
            }
        }

        // ===============================================================
        // 第四批：团队半径 / 发放物品类
        // ===============================================================

        /// <summary>【开路】每次进关自动装备一只装满 4 件物资的背包（先驱）。</summary>
        private sealed class PerkBackpackStart : Perk
        {
            private static readonly string[] Items =
            {
                "RopeSpool", "ClimbingSpike", "Bandages", "FirstAidKit",
                "Energy Drink", "Torch", "Lantern", "TrailMix",
            };

            public override string Id => "backpackStart";
            public override string NameZh => "开路";
            public override string DescZh => "每次进入新关卡自动装备一只装着 4 件随机物资的背包。";
            public override string DescEn => "Every new level you are given a backpack filled with 4 random supplies.";

            public override void OnDraw(Character character, string key)
            {
                var list = new List<string>(4);
                for (int i = 0; i < 4; i++) list.Add(Items[UnityEngine.Random.Range(0, Items.Length)]);
                HostApi.EquipBackpack(character, list);
            }
        }

        /// <summary>【护理】每 8 秒为半径 5 米内的玩家 −2.5 损伤（医生）。</summary>
        private sealed class PerkNursing : Perk
        {
            public override string Id => "nursing";
            public override string NameZh => "护理";
            public override string DescZh => "每 8 秒为半径 5 米内的所有玩家 −2.5 损伤（含自己）。";
            public override string DescEn => "Every 8 s, all players within 5 m (including you) get −2.5 injury.";

            public override void Tick(Character character, string key, float dt)
            {
                if (!Due(key, dt, 8f)) return;
                foreach (var target in HostApi.GetCharactersInRadius(character.Center, 5f))
                {
                    if (target == null || IsDead(target)) continue;
                    HostApi.ApplyStatus(target, CharacterAfflictions.STATUSTYPE.Injury, -0.025f);
                }
            }
        }

        /// <summary>【解毒】每 3 秒为半径 5 米内的玩家 −2.5 中毒 / −2.5 孢子（医生）。</summary>
        private sealed class PerkDetox : Perk
        {
            public override string Id => "detox";
            public override string NameZh => "解毒";
            public override string DescZh => "每 3 秒为半径 5 米内的所有玩家 −2.5 中毒与 −2.5 孢子。";
            public override string DescEn => "Every 3 s, all players within 5 m get −2.5 poison and −2.5 spores.";

            public override void Tick(Character character, string key, float dt)
            {
                if (!Due(key, dt, 3f)) return;
                foreach (var target in HostApi.GetCharactersInRadius(character.Center, 5f))
                {
                    if (target == null || IsDead(target)) continue;
                    HostApi.ApplyStatus(target, CharacterAfflictions.STATUSTYPE.Poison, -0.025f);
                    HostApi.ApplyStatus(target, CharacterAfflictions.STATUSTYPE.Spores, -0.025f);
                }
            }
        }

        /// <summary>【号召】每 3 分钟为半径 30 米内的玩家 +25 额外体力、−5 损伤、−10 饥饿（领袖）。</summary>
        private sealed class PerkRally : Perk
        {
            public override string Id => "rally";
            public override string NameZh => "号召";
            public override string DescZh => "每 3 分钟为半径 30 米内的所有玩家 +25 额外体力、−5 损伤、−10 饥饿（含自己）。";
            public override string DescEn => "Every 3 min, all players within 30 m (including you) get +25 extra stamina, −5 injury, −10 hunger.";

            public override void Tick(Character character, string key, float dt)
            {
                if (!Due(key, dt, 180f)) return;
                foreach (var target in HostApi.GetCharactersInRadius(character.Center, 30f))
                {
                    if (target == null || IsDead(target)) continue;
                    HostApi.BoostExtraStamina(target, 0.25f);
                    HostApi.ApplyStatus(target, CharacterAfflictions.STATUSTYPE.Injury, -0.05f);
                    HostApi.ApplyStatus(target, CharacterAfflictions.STATUSTYPE.Hunger, -0.10f);
                }
                HostClassManager.Instance.ShowToast($"【号召】{Nick(character)} 鼓舞了周围的同伴");
            }
        }

        /// <summary>【自然韧性】每 5 秒 −2.5 中毒 / −2.5 孢子（野人）。</summary>
        private sealed class PerkNaturalTenacity : Perk
        {
            public override string Id => "naturalTenacity";
            public override string NameZh => "自然韧性";
            public override string DescZh => "每 5 秒 −2.5 中毒与 −2.5 孢子。";
            public override string DescEn => "Every 5 s: −2.5 poison and −2.5 spores.";

            public override void Tick(Character character, string key, float dt)
            {
                if (!Due(key, dt, 5f)) return;
                HostApi.ApplyStatus(character, CharacterAfflictions.STATUSTYPE.Poison, -0.025f);
                HostApi.ApplyStatus(character, CharacterAfflictions.STATUSTYPE.Spores, -0.025f);
            }
        }

        /// <summary>【周期粉蕉】每 60 秒获得一根粉蕉（野人）。</summary>
        private sealed class PerkBananaCycle : Perk
        {
            public override string Id => "bananaCycle";
            public override string NameZh => "周期粉蕉";
            public override string DescZh => "每 60 秒获得一根粉蕉（优先放入空物品栏）。";
            public override string DescEn => "One pink banana every 60 s (empty inventory slot first).";

            public override void Tick(Character character, string key, float dt)
            {
                if (!Due(key, dt, 60f)) return;
                if (!HostApi.GiveItemToInventorySlots(character, ItemNames.BerrynanaPink, out _))
                    HostApi.SpawnItemAtFeet(character, ItemNames.BerrynanaPink);
            }
        }

        /// <summary>【完美主义】每 60 秒 +100 额外体力（圣人）。</summary>
        private sealed class PerkPerfectionism : Perk
        {
            public override string Id => "perfectionism";
            public override string NameZh => "完美主义";
            public override string DescZh => "每 60 秒自己 +100 额外体力。";
            public override string DescEn => "+100 extra stamina every 60 s.";

            public override void Tick(Character character, string key, float dt)
            {
                if (!Due(key, dt, 60f)) return;
                HostApi.BoostExtraStamina(character, 1f);
            }
        }

        /// <summary>【饮食供给】半径内每名玩家 +5 额外体力（炊事员），每 20 秒一次。</summary>
        private sealed class PerkFoodSupply : Perk
        {
            public override string Id => "foodSupply";
            public override string NameZh => "饮食供给";
            public override string DescZh => "每 20 秒为半径 10 米内的所有玩家 +5 额外体力。";
            public override string DescEn => "Every 20 s, all players within 10 m get +5 extra stamina.";

            public override void Tick(Character character, string key, float dt)
            {
                if (!Due(key, dt, 20f)) return;
                foreach (var target in HostApi.GetCharactersInRadius(character.Center, 10f))
                {
                    if (target == null || IsDead(target)) continue;
                    HostApi.BoostExtraStamina(target, 0.05f);
                }
            }
        }

        /// <summary>【开炮】每关获得 1 门童子军大炮，之后每 3 分钟再获得 1 门（炮手）。</summary>
        private sealed class PerkCannonCycle : Perk
        {
            public override string Id => "cannonCycle";
            public override string NameZh => "开炮";
            public override string DescZh => "每次进入新关卡获得 1 门童子军大炮，之后每 3 分钟再获得 1 门。";
            public override string DescEn => "One scout cannon every level, plus one more every 3 min.";

            public override void OnDraw(Character character, string key) => Give(character);

            public override void Tick(Character character, string key, float dt)
            {
                if (!Due(key, dt, 180f)) return;
                Give(character);
            }

            private static void Give(Character character)
            {
                if (!HostApi.GiveItemToInventorySlots(character, ItemNames.ScoutCannonItem, out _))
                    HostApi.SpawnItemAtFeet(character, ItemNames.ScoutCannonItem);
            }
        }

        /// <summary>【好多区】半径内每有一名其他玩家就 +2.5 额外体力（区），每 20 秒结算，最多 4 层。</summary>
        private sealed class PerkManyQu : Perk
        {
            public override string Id => "manyQu";
            public override string NameZh => "好多区";
            public override string DescZh => "每 20 秒结算一次：半径 15 米内每有一名其他玩家，自身 +2.5 额外体力（最多 4 层）。";
            public override string DescEn => "Every 20 s: +2.5 extra stamina per other player within 15 m (up to 4 stacks).";

            public override void Tick(Character character, string key, float dt)
            {
                if (!Due(key, dt, 20f)) return;
                int nearby = Mathf.Min(4, PerkNearbyOthers(character, 15f));
                if (nearby <= 0) return;
                HostApi.BoostExtraStamina(character, 0.025f * nearby);
            }
        }

        /// <summary>【温暖臂膀】每 6 秒为半径 6 米内的玩家 −2.5 损伤（救火队员）。</summary>
        private sealed class PerkWarmShoulder : Perk
        {
            public override string Id => "warmShoulder";
            public override string NameZh => "温暖臂膀";
            public override string DescZh => "每 6 秒为半径 6 米内的所有玩家 −2.5 损伤（含自己）。";
            public override string DescEn => "Every 6 s, all players within 6 m (including you) get −2.5 injury.";

            public override void Tick(Character character, string key, float dt)
            {
                if (!Due(key, dt, 6f)) return;
                foreach (var target in HostApi.GetCharactersInRadius(character.Center, 6f))
                {
                    if (target == null || IsDead(target)) continue;
                    HostApi.ApplyStatus(target, CharacterAfflictions.STATUSTYPE.Injury, -0.025f);
                }
            }
        }

        // ===============================================================
        // 第五批：剩余定时 / 数值类
        // ===============================================================

        /// <summary>【安抚】每 60 秒为半径 20 米内的玩家 −30 困倦（领袖）。</summary>
        private sealed class PerkSoothing : Perk
        {
            public override string Id => "soothing";
            public override string NameZh => "安抚";
            public override string DescZh => "每 60 秒为半径 20 米内的所有玩家 −30 困倦（含自己）。";
            public override string DescEn => "Every 60 s, all players within 20 m get −30 drowsiness.";

            public override void Tick(Character character, string key, float dt)
            {
                if (!Due(key, dt, 60f)) return;
                foreach (var target in HostApi.GetCharactersInRadius(character.Center, 20f))
                {
                    if (target == null || IsDead(target)) continue;
                    HostApi.ApplyStatus(target, CharacterAfflictions.STATUSTYPE.Drowsy, -0.30f);
                }
            }
        }

        /// <summary>【同伴减伤】15 米内有同伴时每 5 秒 −2.5 损伤（童军）。</summary>
        private sealed class PerkPeerInjuryReduce : Perk
        {
            public override string Id => "peerInjuryReduce";
            public override string NameZh => "同伴减伤";
            public override string DescZh => "15 米内有其他玩家时，每 5 秒 −2.5 损伤。";
            public override string DescEn => "While another player is within 15 m, −2.5 injury every 5 s.";

            public override void Tick(Character character, string key, float dt)
            {
                if (!Due(key, dt, 5f)) return;
                if (PerkNearbyOthers(character, 15f) == 0) return;
                HostApi.ApplyStatus(character, CharacterAfflictions.STATUSTYPE.Injury, -0.025f);
            }
        }

        /// <summary>【辟谷养生】每个关卡开始时饥饿清零（圣人）。</summary>

        /// <summary>【教诲】每 3 秒为半径 15 米内的其他玩家 +2.5 额外体力（圣人）。</summary>
        private sealed class PerkTeaching : Perk
        {
            public override string Id => "teaching";
            public override string NameZh => "教诲";
            public override string DescZh => "每 3 秒为半径 15 米内的其他玩家 +2.5 额外体力。";
            public override string DescEn => "Every 3 s, other players within 15 m get +2.5 extra stamina.";

            public override void Tick(Character character, string key, float dt)
            {
                if (!Due(key, dt, 3f)) return;
                foreach (var target in HostApi.GetCharactersInRadius(character.Center, 15f))
                {
                    if (target == null || target == character || IsDead(target)) continue;
                    HostApi.BoostExtraStamina(target, 0.025f);
                }
            }
        }

        /// <summary>【点化】每 20 秒为半径 10 米内的玩家 +50 额外体力（法师）。</summary>
        private sealed class PerkEnlighten : Perk
        {
            public override string Id => "enlighten";
            public override string NameZh => "点化";
            public override string DescZh => "每 20 秒为半径 10 米内的所有玩家 +50 额外体力（含自己）。";
            public override string DescEn => "Every 20 s, all players within 10 m get +50 extra stamina.";

            public override void Tick(Character character, string key, float dt)
            {
                if (!Due(key, dt, 20f)) return;
                foreach (var target in HostApi.GetCharactersInRadius(character.Center, 10f))
                {
                    if (target == null || IsDead(target)) continue;
                    HostApi.BoostExtraStamina(target, 0.5f);
                }
            }
        }

        /// <summary>【极限】开局自带高诅咒与高石化（挑战者）。</summary>
        private sealed class PerkExtreme : Perk
        {
            public override string Id => "extreme";
            public override string NameZh => "极限";
            public override string DescZh => "开局自带 40 诅咒、40 石化，并获得 1 把黄金抓钩；没有本词条的角色会被收回黄金抓钩。";
            public override string DescEn => "Start with 40 curse, 40 petrify and a golden grapnel; anyone without this perk has their golden grapnel removed.";

            public override void OnDraw(Character character, string key)
            {
                HostApi.ApplyStatus(character, CharacterAfflictions.STATUSTYPE.Curse, 0.40f);
                HostApi.SetPetrify(character, 40);
                Give(character, ItemNames.GoldenHook);
            }
        }

        /// <summary>【炸膛】每次受到损伤增加时获得 1 个炸药（炮手）。</summary>
        private sealed class PerkBlowback : Perk
        {
            public override string Id => "blowback";
            public override string NameZh => "炸膛";
            public override string DescZh => "每次受到损伤增加时获得 1 个炸药（优先放入空物品栏，满了塞脚下）。";
            public override string DescEn => "Every time you take injury you gain 1 dynamite.";

            public override void Tick(Character character, string key, float dt)
            {
                float injury = HostApi.GetStatus(character, CharacterAfflictions.STATUSTYPE.Injury);
                float last = State(key);
                SetState(key, injury);
                if (injury - last <= 0.005f) return;

                if (!HostApi.GiveItemToInventorySlots(character, ItemNames.Dynamite, out _))
                    HostApi.SpawnItemAtFeet(character, ItemNames.Dynamite);
            }
        }

        /// <summary>【独行回体】50 米内无人时每 30 秒 +10 额外体力（独狼）。</summary>
        private sealed class PerkSoloStamina : Perk
        {
            public override string Id => "soloStamina";
            public override string NameZh => "独行回体";
            public override string DescZh => "50 米内没有其他玩家时，每 30 秒 +10 额外体力。";
            public override string DescEn => "While no other player is within 50 m, +10 extra stamina every 30 s.";

            public override void Tick(Character character, string key, float dt)
            {
                if (!Due(key, dt, 30f)) return;
                if (PerkNearbyOthers(character, 50f) > 0) return;
                HostApi.BoostExtraStamina(character, 0.10f);
            }
        }

        /// <summary>【NASA】每 5 秒 −2.5 寒冷与 −2.5 炎热（太空人）。</summary>
        private sealed class PerkNasa : Perk
        {
            public override string Id => "nasa";
            public override string NameZh => "NASA";
            public override string DescZh => "每 5 秒 −2.5 寒冷与 −2.5 炎热。";
            public override string DescEn => "Every 5 s: −2.5 Cold and −2.5 Hot.";

            public override void Tick(Character character, string key, float dt)
            {
                if (!Due(key, dt, 5f)) return;
                HostApi.ApplyStatus(character, CharacterAfflictions.STATUSTYPE.Cold, -0.025f);
                HostApi.ApplyStatus(character, CharacterAfflictions.STATUSTYPE.Hot, -0.025f);
            }
        }

        /// <summary>【精品大果】每 100 秒获得 1 个荔莓（榔王）。</summary>
        private sealed class PerkKingberry : Perk
        {
            public override string Id => "kingberry";
            public override string NameZh => "精品大果";
            public override string DescZh => "每 100 秒获得 1 个荔莓（优先放入空物品栏）。";
            public override string DescEn => "One kingberry every 100 s (empty inventory slot first).";

            public override void Tick(Character character, string key, float dt)
            {
                if (!Due(key, dt, 100f)) return;
                string berry = ModConfig.Langwang.GreenBerryItem.Value;
                if (string.IsNullOrEmpty(berry)) return;
                if (!HostApi.GiveItemToInventorySlots(character, berry, out _))
                    HostApi.SpawnItemAtFeet(character, berry);
            }
        }

        /// <summary>【带妻出行】每次进关获得 1 条绳索（ATacv）。</summary>
        private sealed class PerkRopeKit : Perk
        {
            public override string Id => "ropeKit";
            public override string NameZh => "带妻出行";
            public override string DescZh => "每次进入新关卡获得 1 条绳索（优先放入空物品栏）。";
            public override string DescEn => "One rope every new level (empty inventory slot first).";

            public override void OnDraw(Character character, string key)
            {
                string rope = ModConfig.ATacv.RopeItem.Value;
                if (string.IsNullOrEmpty(rope)) return;
                if (!HostApi.GiveItemToInventorySlots(character, rope, out _))
                    HostApi.SpawnItemAtFeet(character, rope);
            }
        }

        // ===============================================================
        // 第六批：死亡事件类
        // ===============================================================

        /// <summary>【共情】队友死亡时自己 +10 损伤（医生，5 秒冷却）。</summary>
        private sealed class PerkEmpathy : Perk
        {
            public override string Id => "empathy";
            public override string NameZh => "共情";
            public override string DescZh => "其他玩家死亡时，自己 +10 损伤（同一时间只结算一次，5 秒冷却）。";
            public override string DescEn => "When another player dies you take +10 injury (settled at most once per 5 s).";

            public override void OnPlayerDeath(Character owner, Character dead)
            {
                if (owner == null || dead == null || dead == owner) return;

                string key = PerkKeyOf(owner);
                if (string.IsNullOrEmpty(key)) return;

                // 冷却：用 State 存「下次可结算时刻」
                if (State(key + "|cd") > Time.time) return;
                SetState(key + "|cd", Time.time + 5f);

                HostApi.ApplyStatus(owner, CharacterAfflictions.STATUSTYPE.Injury, 0.10f);
                Plugin.Log.LogInfo($"[RollOfTheScout] 共情：{Nick(dead)} 死亡，{Nick(owner)} 损伤 +10。");
            }
        }

        /// <summary>【士气】自己死亡时，所有存活玩家 +10 诅咒 +10 石化 +30 额外体力（领袖）。</summary>
        private sealed class PerkMorale : Perk
        {
            public override string Id => "morale";
            public override string NameZh => "士气";
            public override string DescZh => "自己死亡时，所有存活玩家 +10 诅咒、+10 石化、+30 额外体力。";
            public override string DescEn => "When you die, every still-living player gains +10 curse, +10 petrify and +30 extra stamina.";

            public override void OnPlayerDeath(Character owner, Character dead)
            {
                if (owner == null || dead != owner) return;

                foreach (var target in HostApi.GetActiveCharacters())
                {
                    if (target == null || target == owner) continue;
                    if (target.data != null && target.data.dead) continue;

                    HostApi.ApplyStatus(target, CharacterAfflictions.STATUSTYPE.Curse, 0.10f);
                    HostApi.ApplyStatus(target, CharacterAfflictions.STATUSTYPE.Petrify, 0.10f);
                    HostApi.BoostExtraStamina(target, 0.30f);
                }
                Plugin.Log.LogInfo($"[RollOfTheScout] 士气：{Nick(owner)} 死亡，为人鼓舞。");
            }
        }

        /// <summary>【亡灵】每有玩家死亡，自己 −5 诅咒 +20 额外体力（死灵法师）。</summary>
        private sealed class PerkUndead : Perk
        {
            public override string Id => "undead";
            public override string NameZh => "亡灵";
            public override string DescZh => "每有一名玩家死亡，自己 −5 诅咒、+20 额外体力。";
            public override string DescEn => "For each player death: −5 curse and +20 extra stamina.";

            public override void OnPlayerDeath(Character owner, Character dead)
            {
                if (owner == null) return;
                HostApi.ApplyStatus(owner, CharacterAfflictions.STATUSTYPE.Curse, -0.05f);
                HostApi.BoostExtraStamina(owner, 0.20f);
            }
        }

        // ===============================================================
        // 第七批：物品发放类
        // ===============================================================

        /// <summary>【骸骨之书】每次进关获得 1 本骸骨之书（死灵法师·骸骨）。</summary>
        private sealed class PerkBoneBook : Perk
        {
            public override string Id => "boneBook";
            public override string NameZh => "骸骨之书";
            public override string DescZh => "每次进入新关卡获得 1 本骸骨之书（优先放入空物品栏）。";
            public override string DescEn => "One Book of Bones every new level (empty inventory slot first).";

            public override void OnDraw(Character character, string key) => Give(character, ItemNames.BookOfBones);
        }

        /// <summary>【魔法卷轴】每 60 秒获得 1 个魔法卷轴（法师）。</summary>
        private sealed class PerkScrollCycle : Perk
        {
            public override string Id => "scrollCycle";
            public override string NameZh => "魔法卷轴";
            public override string DescZh => "每 60 秒获得 1 个魔法卷轴（优先放入空物品栏）。";
            public override string DescEn => "One magic scroll every 60 s (empty inventory slot first).";

            public override void Tick(Character character, string key, float dt)
            {
                if (!Due(key, dt, 60f)) return;
                Give(character, ItemNames.GuidebookScroll);
            }
        }

        /// <summary>【顶级玩家】每次进关获得 1 把黄金抓钩（挑战者）。</summary>
        private sealed class PerkGoldenHook : Perk
        {
            public override string Id => "goldenHook";
            public override string NameZh => "顶级玩家";
            public override string DescZh => "每次进入新关卡获得 3 把救援抓钩（优先放入空物品栏）。";
            public override string DescEn => "Three rescue hooks every new level (empty inventory slot first).";

            public override void OnDraw(Character character, string key)
            {
                for (int i = 0; i < 3; i++) Give(character, "RescueHook");
            }
        }

        /// <summary>【自带厨具】每次进关获得 1 个便携烤炉（特级厨师 / 炊事员）。</summary>
        private sealed class PerkPortableStove : Perk
        {
            public override string Id => "portableStove";
            public override string NameZh => "自带厨具";
            public override string DescZh => "每次进入新关卡获得 1 个便携烤炉（优先放入空物品栏）。";
            public override string DescEn => "One portable stove every new level (empty inventory slot first).";

            public override void OnDraw(Character character, string key) => Give(character, ItemNames.PortableStove);
        }

        /// <summary>【我全都要】每次进关直接装备一只背包（仓鼠）。</summary>
        private sealed class PerkBackpackAll : Perk
        {
            public override string Id => "backpackAll";
            public override string NameZh => "我全都要";
            public override string DescZh => "每次进入新关卡自动装备一只背包。";
            public override string DescEn => "You are equipped with a backpack at the start of every level.";

            public override void OnDraw(Character character, string key)
            {
                if (!HostApi.EquipBackpack(character, new List<string>()))
                    Give(character, ItemNames.Backpack);
            }
        }

        // ===============================================================
        // 第八批：表情驱动类
        // ===============================================================

        /// <summary>【舞动青春】自己跳舞时，自己与半径内的玩家各 +2.5 额外体力（舞王）。</summary>
        private sealed class PerkDanceYouth : Perk
        {
            public override string Id => "danceYouth";
            public override string NameZh => "舞动青春";
            public override string DescZh => "自己跳舞时，自己与半径 10 米内的玩家各 +2.5 额外体力。";
            public override string DescEn => "When you dance, you and every player within 10 m gain +2.5 extra stamina.";

            public void Trigger(Character dancer)
            {
                foreach (var target in HostApi.GetCharactersInRadius(dancer.Center, 10f))
                {
                    if (target == null || IsDead(target)) continue;
                    HostApi.BoostExtraStamina(target, 0.025f);
                }
            }
        }

        /// <summary>【全场欢呼】自己跳舞时，强制半径 10 米内的其他玩家一起跳舞（舞王）。</summary>
        private sealed class PerkCrowdCheer : Perk
        {
            public override string Id => "crowdCheer";
            public override string NameZh => "全场欢呼";
            public override string DescZh => "自己跳舞时，强制半径 10 米内的其他玩家一起跳舞。";
            public override string DescEn => "When you dance, every other player within 10 m is forced to dance too.";

            public void Trigger(Character dancer)
            {
                HostClassManager.Instance.ForceNearbyPlayersToDance(dancer);
            }
        }

        // ===============================================================
        // 第九批：周期净化类（参数化模板）
        // ===============================================================

        /// <summary>周期性地削减某一项状态的词条（负数即治疗/净化）。</summary>
        private sealed class PerkDrain : Perk
        {
            private readonly string _id;
            private readonly string _nameZh;
            private readonly string _descZh;
            private readonly string _descEn;
            private readonly CharacterAfflictions.STATUSTYPE _status;
            private readonly float _amount;
            private readonly float _interval;

            public PerkDrain(string id, string nameZh, string descZh, string descEn,
                CharacterAfflictions.STATUSTYPE status, float amount, float interval)
            {
                _id = id;
                _nameZh = nameZh;
                _descZh = descZh;
                _descEn = descEn;
                _status = status;
                _amount = amount;
                _interval = interval;
            }

            public override string Id => _id;
            public override string NameZh => _nameZh;
            public override string DescZh => _descZh;
            public override string DescEn => _descEn;

            public override void Tick(Character character, string key, float dt)
            {
                if (!Due(key, dt, _interval)) return;
                HostApi.ApplyStatus(character, _status, _amount);
            }
        }

        // ===============================================================
        // 第十一批：条件型词条
        // ===============================================================

        /// <summary>【急救箱】每次进关装备一只医疗背包（医生·随队医护）。</summary>
        private sealed class PerkMedKit : Perk
        {
            public override string Id => "medKit";
            public override string NameZh => "急救箱";
            public override string DescZh => "每次进入新关卡自动装备一只装 4 件医疗物资的背包。";
            public override string DescEn => "Every new level you get a backpack with 4 medical supplies.";

            public override void OnDraw(Character character, string key)
            {
                var items = new List<string> { "Bandages", "FirstAidKit", "MedicinalRoot", "Antidote" };
                if (HostApi.EquipBackpack(character, items)) return;
                foreach (var item in items) Give(character, item);
            }
        }

        /// <summary>【跌落神坛】损伤过高时持续积累诅咒（圣人）。</summary>
        private sealed class PerkFallFromGrace : Perk
        {
            public override string Id => "fallFromGrace";
            public override string NameZh => "跌落神坛";
            public override string DescZh => "自身损伤高于 75 时，每 10 秒 +5 诅咒（把损伤降下来即可停止）。";
            public override string DescEn => "While your injury is above 75, +5 curse every 10 s (heal up to stop it).";

            public override void Tick(Character character, string key, float dt)
            {
                float injury = HostApi.GetStatus(character, CharacterAfflictions.STATUSTYPE.Injury);
                if (injury < 0.75f) return;
                if (!Due(key, dt, 10f)) return;
                HostApi.ApplyStatus(character, CharacterAfflictions.STATUSTYPE.Curse, 0.05f);
            }
        }

        /// <summary>【手暖炉】带着便携烤炉就会持续受热（特级厨师）。</summary>
        private sealed class PerkStoveHeat : Perk
        {
            public override string Id => "stoveHeat";
            public override string NameZh => "手暖炉";
            public override string DescZh => "物品栏里带着便携烤炉时，每 10 秒 +2.5 炎热（把烤炉丢掉即可停止）。";
            public override string DescEn => "While a portable stove is in your inventory, +2.5 Hot every 10 s (drop it to stop).";

            public override void Tick(Character character, string key, float dt)
            {
                if (!Due(key, dt, 10f)) return;
                if (CountItemInInventory(character, ItemNames.PortableStove) <= 0) return;
                HostApi.ApplyStatus(character, CharacterAfflictions.STATUSTYPE.Hot, 0.025f);
            }
        }

        // ===============================================================
        // 第十二批：拾取层（由 Patches.PerkPickupPatch 调用）
        // ===============================================================

        /// <summary>把预制体名解析成 itemID（拾取改写用）。</summary>
        private static bool TryGetItemId(string prefabName, out ushort itemId)
        {
            itemId = 0;
            if (string.IsNullOrEmpty(prefabName)) return false;
            if (!ItemDatabase.TryGetItem(prefabName, out Item prefab) || prefab == null) return false;
            itemId = prefab.itemID;
            return true;
        }

        /// <summary>
        /// 拾取层总入口：持有拾取类词条时**就地改写** <paramref name="itemID"/>。
        /// 只改写、不取消拾取，因此不会触发原版「拒绝拾取」把物品丢回地面的分支。
        /// </summary>
        internal void TryPerkPickup(Character character, ref ushort itemID)
        {
            if (!ModConfig.Perk.Enabled.Value || character == null) return;

            var list = PerksOf(character);
            if (list == null) return;

            foreach (var perk in list)
            {
                if (perk is PerkAssassinPick)
                {
                    // 10% 概率把拾取物换成仪式匕首（与刺客职业同款数值）
                    if (UnityEngine.Random.value >= 0.10f) return;
                    if (!TryGetItemId("RitualDagger", out ushort daggerId)) return;

                    string before = PerkItemNameOf(null);
                    itemID = daggerId;
                    Plugin.Log.LogInfo($"[RollOfTheScout] 刺杀：{Nick(character)} 拾取物被换成仪式匕首。");
                    return;
                }
            }
        }

        /// <summary>【刺杀】拾取物品有 10% 概率变成仪式匕首（刺客）。</summary>
        private sealed class PerkAssassinPick : Perk
        {
            public override string Id => "assassinPick";
            public override string NameZh => "刺杀";
            public override string DescZh => "拾取任意物品时有 10% 概率变成仪式匕首。";
            public override string DescEn => "Every picked-up item has a 10% chance to become a ritual dagger.";
        }
        /// <summary>开箱（Luggage）事件入口，由 Patches.PerkLuggagePatch 调用。</summary>
        internal void TryPerkLuggageOpened(Character character)
        {
            if (!ModConfig.Perk.Enabled.Value || character == null) return;

            var list = PerksOf(character);
            if (list == null) return;

            foreach (var perk in list)
            {
                if (!(perk is PerkPharaohCurse)) continue;
                HostApi.ApplyStatus(character, CharacterAfflictions.STATUSTYPE.Curse, 0.025f);
                Plugin.Log.LogInfo($"[RollOfTheScout] 法老的诅咒：{Nick(character)} 开箱，诅咒 +2.5。");
                return;
            }
        }

        /// <summary>【法老的诅咒】每次开箱（Luggage）＋2.5 诅咒。</summary>
        private sealed class PerkPharaohCurse : Perk
        {
            public override string Id => "pharaohCurse";
            public override string NameZh => "法老的诅咒";
            public override string DescZh => "每次打开箱子 / 行李时 +2.5 诅咒。";
            public override string DescEn => "+2.5 curse every time you open a chest / luggage.";
        }

        // ===============================================================
        // 第二批词条：状态惩罚 / 环境惩罚
        // ===============================================================

        /// <summary>【深度冻伤】携带寒冷时跑跳每 5 秒 +5 损伤（与外皮烧伤互斥）。</summary>
        private sealed class PerkDeepFrostbite : Perk
        {
            public override string Id => "deepFrostbite";
            public override string NameZh => "深度冻伤";
            public override string DescZh => "携带寒冷时，跑动/跳跃状态下每 5 秒 +5 损伤。无法同时获得【外皮烧伤】。";
            public override string DescEn => "While carrying Cold, +5 injury every 5 s while running/jumping. Cannot coexist with 【Skin Burn】.";
            public override string ExcludeGroup => "thermalInjury";

            public override void Tick(Character character, string key, float dt)
            {
                if (character.data == null) return;
                float cold = HostApi.GetStatus(character, CharacterAfflictions.STATUSTYPE.Cold);
                if (cold <= 0.01f) return;
                if (!(character.data.isSprinting || character.data.isJumping)) return;

                if (!Due(key, dt, 5f)) return;
                HostApi.ApplyStatus(character, CharacterAfflictions.STATUSTYPE.Injury, 0.05f);
            }
        }

        /// <summary>【外皮烧伤】携带灼烧时攀爬每 5 秒 +2.5 损伤（与深度冻伤互斥）。</summary>
        private sealed class PerkSkinBurn : Perk
        {
            public override string Id => "skinBurn";
            public override string NameZh => "外皮烧伤";
            public override string DescZh => "携带灼烧时，攀爬状态下每 5 秒 +2.5 损伤。无法同时获得【深度冻伤】。";
            public override string DescEn => "While carrying Hot, +2.5 injury every 5 s while climbing. Cannot coexist with 【Deep Frostbite】.";
            public override string ExcludeGroup => "thermalInjury";

            public override void Tick(Character character, string key, float dt)
            {
                float hot = HostApi.GetStatus(character, CharacterAfflictions.STATUSTYPE.Hot);
                if (hot <= 0.01f) return;
                if (!PerkIsClimbing(character)) return;

                if (!Due(key, dt, 5f)) return;
                HostApi.ApplyStatus(character, CharacterAfflictions.STATUSTYPE.Injury, 0.025f);
            }
        }

        /// <summary>【肉体穿刺】受到尖刺时，等量转化为损伤。</summary>
        private sealed class PerkFleshPiercing : Perk
        {
            public override string Id => "fleshPiercing";
            public override string NameZh => "肉体穿刺";
            public override string DescZh => "受到尖刺时，增加等量损伤。";
            public override string DescEn => "Thorns you receive are converted into an equal amount of injury.";

            public override void Tick(Character character, string key, float dt)
            {
                float thorns = HostApi.GetStatus(character, CharacterAfflictions.STATUSTYPE.Thorns);
                float last = State(key);
                SetState(key, thorns);

                float delta = thorns - last;
                if (delta <= 0.0005f) return;

                HostApi.ApplyStatus(character, CharacterAfflictions.STATUSTYPE.Injury, delta);
                Plugin.Log.LogInfo($"[RollOfTheScout] 肉体穿刺：{Nick(character)} 受到尖刺 {delta * 100f:0.#}，等量转为损伤。");
            }
        }

        /// <summary>【肢体僵硬】后空翻必定失败（由 Patches.PerkBackflipFailPatch 把 succeeded 改成 false）。</summary>
        private sealed class PerkStiffLimb : Perk
        {
            public override string Id => "stiffLimb";
            public override string NameZh => "肢体僵硬";
            public override string DescZh => "做出后空翻必定失败（原版会判定为失败并摔下）。";
            public override string DescEn => "Your backflips always fail.";
        }

        /// <summary>【真假死】使用装死动作直接死亡。</summary>
        private sealed class PerkFakeDeath : Perk
        {
            public override string Id => "fakeDeath";
            public override string NameZh => "真假死";
            public override string DescZh => "使用「装死」动作时直接死亡。";
            public override string DescEn => "Performing the play-dead emote kills you for real.";

            public override void OnDraw(Character character, string key)
            {
                HostClassManager.Instance.ShowToast($"【怪谈】{Nick(character)} 抽到了「真假死」——装死要真的死");
            }

            /// <summary>装死触发：前两次是递增损伤，第三次起秒杀。</summary>
            public bool Trigger(Character character, string key)
                => EscalatingPunish(character, key, "真假死");
        }

        /// <summary>【猜忌】身边 10 米内有其他玩家时，每 5 秒 +2.5 中毒（与过度依赖互斥）。</summary>
        private sealed class PerkJealousy : Perk
        {
            public override string Id => "jealousy";
            public override string NameZh => "猜忌";
            public override string DescZh => "10 米内有其他玩家时，每 5 秒 +2.5 中毒。无法同时获得【过度依赖】。";
            public override string DescEn => "While another player is within 10 m, +2.5 poison every 5 s. Cannot coexist with 【Overdependence】.";
            public override string ExcludeGroup => "company";

            public override void Tick(Character character, string key, float dt)
            {
                if (!Due(key, dt, 5f)) return;
                if (PerkNearbyOthers(character, 10f) == 0) return;
                HostApi.ApplyStatus(character, CharacterAfflictions.STATUSTYPE.Poison, 0.025f);
            }
        }

        /// <summary>【过度依赖】50 米内没有其他玩家时，每 20 秒「动弹不得」（与猜忌互斥）。</summary>
        private sealed class PerkOverdependence : Perk
        {
            public override string Id => "overdependence";
            public override string NameZh => "过度依赖";
            public override string DescZh => "50 米内没有其他玩家时，每 20 秒动弹不得一次。无法同时获得【猜忌】。";
            public override string DescEn => "While no other player is within 50 m, you are paralysed once every 20 s. Cannot coexist with 【Jealousy】.";
            public override string ExcludeGroup => "company";

            public override void Tick(Character character, string key, float dt)
            {
                if (!Due(key, dt, 20f)) return;
                if (PerkNearbyOthers(character, 50f) > 0) return;
                HostClassManager.Instance.PerkBadMushroomEffect(character, "词条·过度依赖",
                    ModConfig.Perk.OverdependenceEffectIndex.Value);
            }
        }

        /// <summary>【过量饮食】饥饿见底还进食时放一次「屁」（坏蘑菇随机效果）。</summary>
        private sealed class PerkOvereating : Perk
        {
            public override string Id => "overeating";
            public override string NameZh => "过量饮食";
            public override string DescZh => "食用物品扣除的饥饿大于你当前拥有的饥饿时，释放一次「屁」（坏蘑菇的随机负面效果）。";
            public override string DescEn => "When eating would deduct more hunger than you currently have, you let out a fart (a random bad-mushroom effect).";
        }

        /// <summary>【力竭】体力耗空时「动弹不得」一次。</summary>
        private sealed class PerkExhaustion : Perk
        {
            public override string Id => "exhaustion";
            public override string NameZh => "力竭";
            public override string DescZh => "体力耗空时获得一次「动弹不得」（坏蘑菇的随机效果）。";
            public override string DescEn => "When your stamina runs out you are paralysed once (a bad-mushroom effect).";

            public override void Tick(Character character, string key, float dt)
            {
                if (character.data == null) return;

                bool empty = character.data.outOfStaminaFor > 0.05f;
                if (!empty)
                {
                    SetState(key, 0f);   // 体力恢复 → 允许下次再触发
                    return;
                }
                if (State(key) > 0f) return;   // 本次力竭已结算
                SetState(key, 1f);
                HostClassManager.Instance.PerkBadMushroomEffect(character, "词条·力竭",
                    ModConfig.Perk.ExhaustionEffectIndex.Value);
            }
        }

        /// <summary>【眼球脆弱】受到寒冷或灼热时致盲一次。</summary>
        private sealed class PerkFragileEyes : Perk
        {
            public override string Id => "fragileEyes";
            public override string NameZh => "眼球脆弱";
            public override string DescZh => "受到寒冷或灼热时获得一次致盲。";
            public override string DescEn => "Receiving Cold or Hot blinds you once.";

            public override void Tick(Character character, string key, float dt)
            {
                float cold = HostApi.GetStatus(character, CharacterAfflictions.STATUSTYPE.Cold);
                float hot = HostApi.GetStatus(character, CharacterAfflictions.STATUSTYPE.Hot);

                float lastCold = State(key + "c");
                float lastHot = State(key + "h");
                SetState(key + "c", cold);
                SetState(key + "h", hot);

                bool gained = cold - lastCold > 0.01f || hot - lastHot > 0.01f;
                if (!gained) return;

                // 冷却 20 秒：寒冷/灼烧是持续累积的，否则会每一帧都致盲
                if (!Due(key + "|cd", dt, 20f)) return;

                HostClassManager.Instance.PerkBlind(character, "词条·眼球脆弱");
            }
        }

        /// <summary>【夜盲症】夜晚每 60 秒致盲一次（与眩光症互斥）。</summary>
        private sealed class PerkNightBlindness : Perk
        {
            public override string Id => "nightBlindness";
            public override string NameZh => "夜盲症";
            public override string DescZh => "夜晚每 60 秒获得一次致盲。无法同时获得【眩光症】。";
            public override string DescEn => "At night you are blinded once every 60 s. Cannot coexist with 【Glare Sensitivity】.";
            public override string ExcludeGroup => "vision";

            public override void Tick(Character character, string key, float dt)
            {
                if (PerkIsDay()) return;
                if (!Due(key, dt, 60f)) return;
                HostClassManager.Instance.PerkBlind(character, "词条·夜盲症");
            }
        }

        /// <summary>【眩光症】白天每 10 秒 +5 困倦（与夜盲症互斥）。</summary>
        private sealed class PerkGlareSensitivity : Perk
        {
            public override string Id => "glareSensitivity";
            public override string NameZh => "眩光症";
            public override string DescZh => "白天且不在阴影下时，每 10 秒 +5 困倦。无法同时获得【夜盲症】。";
            public override string DescEn => "During the day (shade is not distinguished yet) +5 drowsiness every 10 s. Cannot coexist with 【Night Blindness】.";
            public override string ExcludeGroup => "vision";

            public override void Tick(Character character, string key, float dt)
            {
                if (!PerkIsDay()) return;
                if (PerkInShadow(character)) return;
                if (!Due(key, dt, 10f)) return;
                HostApi.ApplyStatus(character, CharacterAfflictions.STATUSTYPE.Drowsy, 0.05f);
            }
        }

        /// <summary>【多动症】每 60 秒强制做一个非装死 / 非后空翻的表情。</summary>
        private sealed class PerkHyperactive : Perk
        {
            private static readonly string[] Emotes =
            {
                "A_Scout_Emote_Nono",
                "A_Scout_Emote_Dance",
                "A_Scout_Emote_Dance1",
                "A_Scout_Emote_Dance2",
            };

            public override string Id => "hyperactive";
            public override string NameZh => "多动症";
            public override string DescZh => "每隔 60 秒强制使用一个表情（不含装死与后空翻）。";
            public override string DescEn => "Every 60 s you are forced to perform an emote (never play-dead or backflip).";

            public override void Tick(Character character, string key, float dt)
            {
                if (!Due(key, dt, 60f)) return;
                HostClassManager.Instance.PerkForceEmote(character, Emotes[UnityEngine.Random.Range(0, Emotes.Length)], true);
            }
        }

        /// <summary>【白化病】白天每 5 秒 +2.5 灼烧。</summary>
        private sealed class PerkAlbinism : Perk
        {
            public override string Id => "albinism";
            public override string NameZh => "白化病";
            public override string DescZh => "白天且不在阴影下时，每 5 秒 +2.5 灼烧。";
            public override string DescEn => "During the day (shade is not distinguished yet) +2.5 Hot every 5 s.";

            public override void Tick(Character character, string key, float dt)
            {
                if (!PerkIsDay()) return;
                if (PerkInShadow(character)) return;
                if (!Due(key, dt, 5f)) return;
                HostApi.ApplyStatus(character, CharacterAfflictions.STATUSTYPE.Hot, 0.025f);
            }
        }

        /// <summary>【筋骨劳损】每次后空翻 +10 损伤。</summary>
        private sealed class PerkBackflipStrain : Perk
        {
            public override string Id => "backflipStrain";
            public override string NameZh => "筋骨劳损";
            public override string DescZh => "使用后空翻时 +10 损伤。";
            public override string DescEn => "Every backflip costs +10 injury.";
        }

        /// <summary>【待皓的祝福】每 120 秒获得一个大大蛋，同时做一次必定失败的后空翻。</summary>
        private sealed class PerkDaihaoBlessing : Perk
        {
            public override string Id => "daihaoBlessing";
            public override string NameZh => "待皓的祝福";
            public override string DescZh => "每 120 秒获得一个大大蛋，并强制做一次失败的后空翻。";
            public override string DescEn => "Every 120 s: gain one big egg and perform a failed backflip.";

            public override void Tick(Character character, string key, float dt)
            {
                if (!Due(key, dt, 120f)) return;

                if (!HostApi.GiveItemToInventorySlots(character, ItemNames.NestEgg, out _))
                    HostApi.SpawnItemAtFeet(character, ItemNames.NestEgg);

                HostClassManager.Instance.PerkForceEmote(character, PerkEmoteBackflip, false);
                Plugin.Log.LogInfo($"[RollOfTheScout] 待皓的祝福：{Nick(character)} 获得大大蛋，并被迫做了一个失败的后空翻。");
            }
        }
    // ===== 成对词条辅助 + 辟谷养生（正/负）=====

        /// <summary>成对词条：抽到其中一个就必须同时给出另一个（如【辟谷养生·正/负】）。</summary>
        private static void EnforcePerkPairs(List<Perk> result, List<Perk> pool)
        {
            Pair(result, pool, "fastingPlus", "fastingMinus");
        }

        private static void Pair(List<Perk> result, List<Perk> pool, string a, string b)
        {
            bool hasA = false, hasB = false;
            foreach (var perk in result)
            {
                if (perk.Id == a) hasA = true;
                else if (perk.Id == b) hasB = true;
            }
            if (hasA == hasB) return;

            string need = hasA ? b : a;
            foreach (var perk in pool)
            {
                if (perk.Id != need) continue;
                result.Add(perk);
                Plugin.Log.LogInfo($"[RollOfTheScout] 成对词条：补上了「{perk.NameZh}」。");
                return;
            }
        }

        /// <summary>【辟谷养生·正】饥饿高于 25 时立刻清零。</summary>
        private sealed class PerkFastingPlus : Perk
        {
            public override string Id => "fastingPlus";
            public override string NameZh => "辟谷养生·正";
            public override string DescZh => "饥饿高于 25 时立刻清零（必定同时获得【辟谷养生·负】）。";
            public override string DescEn => "Whenever hunger goes above 25 it is reset to 0 (always paired with the negative half).";

            public override void Tick(Character character, string key, float dt)
            {
                if (!Due(key, dt, 1f)) return;
                float hunger = HostApi.GetStatus(character, CharacterAfflictions.STATUSTYPE.Hunger);
                if (hunger <= 0.25f) return;
                HostApi.SetStatusAbsolute(character, CharacterAfflictions.STATUSTYPE.Hunger, 0f);
            }
        }

        /// <summary>【辟谷养生·负】捡起的食物自动消失。</summary>
        /// <summary>食物名判定（辟谷养生·负 用）：原版没有统一的 IsFood，按预制体名关键词匹配。</summary>
        internal static bool PerkIsFoodName(string itemName)
        {
            if (string.IsNullOrEmpty(itemName)) return false;
            foreach (var keyword in PerkFastingMinus.FoodKeywords)
            {
                if (itemName.IndexOf(keyword, System.StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        /// <summary>
        /// 拾取层：持有【辟谷养生·负】时，食物在**拾取瞬间**被取消（返回 true 表示已拦截）。
        /// 由 Patches.PerkVanishPickupPatch 调用（挂 Item.RequestPickup，可取消拾取）。
        /// </summary>
        internal bool TryPerkVanishPickup(Item item, Character picker)
        {
            if (!ModConfig.Perk.Enabled.Value || item == null || picker == null) return false;
            if (!HasPerk(picker, "fastingMinus")) return false;

            string itemName = PerkItemNameOf(item);
            if (!PerkIsFoodName(itemName)) return false;

            Plugin.Log.LogInfo($"[RollOfTheScout] 辟谷养生·负：{Nick(picker)} 拾取的食物 {itemName} 直接消失。");
            return true;
        }

        private sealed class PerkFastingMinus : Perk
        {
            internal static readonly string[] FoodKeywords =
            {
                "Berry", "Banana", "Muffin", "Marshmallow", "Cookie", "Candy", "Food", "Egg",
                "Milk", "Drink", "Soup", "Stew", "Meat", "Fish", "Mushroom", "Root", "Aloe",
                "Granola", "TrailMix", "Lollipop", "Cheese", "Bread", "Apple", "Coconut",
            };

            public override string Id => "fastingMinus";
            public override string NameZh => "辟谷养生·负";
            public override string DescZh => "捡起的食物会自动消失（必定同时获得【辟谷养生·正】）。";
            public override string DescEn => "Any food you pick up vanishes automatically (always paired with the positive half).";

            public override void Tick(Character character, string key, float dt)
            {
                if (!Due(key, dt, 1f)) return;
                if (character.player == null || character.player.itemSlots == null) return;

                foreach (var slot in character.player.itemSlots)
                {
                    if (slot == null || slot.IsEmpty()) continue;
                    var prefab = slot.prefab;
                    if (prefab == null) continue;

                    string itemName = prefab.gameObject.name;
                    if (!IsFoodName(itemName)) continue;

                    int removed = HostApi.RemoveItemsFromInventorySlots(character, itemName);
                    if (removed > 0)
                        Plugin.Log.LogInfo($"[RollOfTheScout] 辟谷养生·负：{Nick(character)} 的 {itemName} 自动消失。");
                    return;
                }
            }

            private static bool IsFoodName(string itemName)
            {
                if (string.IsNullOrEmpty(itemName)) return false;
                foreach (var keyword in FoodKeywords)
                {
                    if (itemName.IndexOf(keyword, System.StringComparison.OrdinalIgnoreCase) >= 0) return true;
                }
                return false;
            }
        }
    }
}
