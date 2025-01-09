﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using SPT.PrePatch;
using BepInEx.Logging;
using Comfort.Common;
using Cysharp.Threading.Tasks;
using Donuts.Models;
using EFT;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;
using IProfileData = GClass592;
using System.Threading;

#pragma warning disable IDE0007, CS4014

namespace Donuts
{
    internal class DonutsBotPrep : MonoBehaviour
    {
        internal static string selectionName;
        internal static string maplocation
        {
            get
            {
                if (Singleton<GameWorld>.Instance == null)
                {
                    return "";
                }

                string location = Singleton<GameWorld>.Instance.MainPlayer.Location.ToLower();

                // Lazy
                if (location == "sandbox_high")
                {
                    location = "sandbox";
                }

                return location;
            }
        }

        internal static string mapName
        {
            get
            {
                switch (maplocation)
                {
                    case "bigmap":
                        return "customs";
                    case "factory4_day":
                        return "factory";
                    case "factory4_night":
                        return "factory_night";
                    case "tarkovstreets":
                        return "streets";
                    case "rezervbase":
                        return "reserve";
                    case "interchange":
                        return "interchange";
                    case "woods":
                        return "woods";
                    case "sandbox":
                    case "sandbox_high":
                        return "groundzero";
                    case "laboratory":
                        return "laboratory";
                    case "lighthouse":
                        return "lighthouse";
                    case "shoreline":
                        return "shoreline";
                    default:
                        return maplocation;
                }
            }
        }

        private static GameWorld gameWorld;
        private static IBotCreator botCreator;
        private static BotSpawner botSpawnerClass;
        private static Player mainplayer;

        internal static Dictionary<string, WildSpawnType> OriginalBotSpawnTypes;

        internal static List<BotSpawnInfo> botSpawnInfos
        {
            get; set;
        }

        private HashSet<string> usedZonesPMC = new HashSet<string>();
        private HashSet<string> usedZonesSCAV = new HashSet<string>();

        public static List<PrepBotInfo> BotInfos
        {
            get; set;
        }

        public static AllMapsZoneConfig allMapsZoneConfig;

        internal static float timeSinceLastReplenish = 0f;

        private bool isReplenishing = false;
        public static bool IsBotPreparationComplete { get; private set; } = false;

        private readonly Dictionary<WildSpawnType, EPlayerSide> spawnTypeToSideMapping = new Dictionary<WildSpawnType, EPlayerSide>
        {
            { WildSpawnType.arenaFighterEvent, EPlayerSide.Savage },
            { WildSpawnType.assault, EPlayerSide.Savage },
            { WildSpawnType.assaultGroup, EPlayerSide.Savage },
            { WildSpawnType.bossBoar, EPlayerSide.Savage },
            { WildSpawnType.bossBoarSniper, EPlayerSide.Savage },
            { WildSpawnType.bossBully, EPlayerSide.Savage },
            { WildSpawnType.bossGluhar, EPlayerSide.Savage },
            { WildSpawnType.bossKilla, EPlayerSide.Savage },
            { WildSpawnType.bossKojaniy, EPlayerSide.Savage },
            { WildSpawnType.bossSanitar, EPlayerSide.Savage },
            { WildSpawnType.bossTagilla, EPlayerSide.Savage },
            { WildSpawnType.bossZryachiy, EPlayerSide.Savage },
            { WildSpawnType.crazyAssaultEvent, EPlayerSide.Savage },
            { WildSpawnType.cursedAssault, EPlayerSide.Savage },
            { WildSpawnType.exUsec, EPlayerSide.Savage },
            { WildSpawnType.followerBoar, EPlayerSide.Savage },
            { WildSpawnType.followerBully, EPlayerSide.Savage },
            { WildSpawnType.followerGluharAssault, EPlayerSide.Savage },
            { WildSpawnType.followerGluharScout, EPlayerSide.Savage },
            { WildSpawnType.followerGluharSecurity, EPlayerSide.Savage },
            { WildSpawnType.followerGluharSnipe, EPlayerSide.Savage },
            { WildSpawnType.followerKojaniy, EPlayerSide.Savage },
            { WildSpawnType.followerSanitar, EPlayerSide.Savage },
            { WildSpawnType.followerTagilla, EPlayerSide.Savage },
            { WildSpawnType.followerZryachiy, EPlayerSide.Savage },
            { WildSpawnType.marksman, EPlayerSide.Savage },
            { WildSpawnType.pmcBot, EPlayerSide.Savage },
            { WildSpawnType.sectantPriest, EPlayerSide.Savage },
            { WildSpawnType.sectantWarrior, EPlayerSide.Savage },
            { WildSpawnType.followerBigPipe, EPlayerSide.Savage },
            { WildSpawnType.followerBirdEye, EPlayerSide.Savage },
            { WildSpawnType.bossKnight, EPlayerSide.Savage },
        };

        internal static ManualLogSource Logger
        {
            get; private set;
        }

        public DonutsBotPrep()
        {
            Logger ??= BepInEx.Logging.Logger.CreateLogSource(nameof(DonutsBotPrep));
        }

        public static void Enable()
        {
            gameWorld = Singleton<GameWorld>.Instance;
            gameWorld.GetOrAddComponent<DonutsBotPrep>();

            Logger.LogDebug("DonutBotPrep Enabled");
        }

        public async void Awake()
        {
            var playerLoop = UnityEngine.LowLevel.PlayerLoop.GetCurrentPlayerLoop();
            Cysharp.Threading.Tasks.PlayerLoopHelper.Initialize(ref playerLoop);

            botSpawnerClass = Singleton<IBotGame>.Instance.BotsController.BotSpawner;
            botCreator = AccessTools.Field(typeof(BotSpawner), "_botCreator").GetValue(botSpawnerClass) as IBotCreator;
            mainplayer = gameWorld?.MainPlayer;
            OriginalBotSpawnTypes = new Dictionary<string, WildSpawnType>();
            BotInfos = new List<PrepBotInfo>();
            botSpawnInfos = new List<BotSpawnInfo>();
            timeSinceLastReplenish = 0;
            IsBotPreparationComplete = false;

            botSpawnerClass.OnBotRemoved += BotSpawnerClass_OnBotRemoved;
            botSpawnerClass.OnBotCreated += BotSpawnerClass_OnBotCreated;

            if (mainplayer != null)
            {
                Logger.LogDebug("Mainplayer is not null, attaching event handlers");
                mainplayer.BeingHitAction += Mainplayer_BeingHitAction;
            }

            // Get selected preset and setup bot limits now
            selectionName = DonutsPlugin.RunWeightedScenarioSelection();

            Logger.LogDebug("selectionName " + selectionName); //JsonConvert.SerializeObject(startingBotConfig));

            Initialization.SetupBotLimit(selectionName);

            var startingBotConfig = DonutComponent.GetStartingBotConfig(selectionName);
            if (startingBotConfig != null)
            {
                Logger.LogDebug("startingBotConfig is not null: " + JsonConvert.SerializeObject(startingBotConfig));

                allMapsZoneConfig = AllMapsZoneConfig.LoadFromDirectory(Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "zoneSpawnPoints"));

                if (allMapsZoneConfig == null)
                {
                    Logger.LogError("Failed to load AllMapsZoneConfig.");
                    return;
                }

                if (string.IsNullOrEmpty(maplocation))
                {
                    Logger.LogError("Map location is null or empty.");
                    return;
                }

                await InitializeAllBotInfos(startingBotConfig, maplocation, cancellationToken: this.GetCancellationTokenOnDestroy());
            }
            else
            {
                Logger.LogError("startingBotConfig is null for selectionName: " + selectionName);
            }

            IsBotPreparationComplete = true;
        }

        private void BotSpawnerClass_OnBotRemoved(BotOwner bot)
        {
            bot.Memory.OnGoalEnemyChanged -= Memory_OnGoalEnemyChanged;
            OriginalBotSpawnTypes.Remove(bot.Profile.Id);
        }
        private void BotSpawnerClass_OnBotCreated(BotOwner bot)
        {
            bot.Memory.OnGoalEnemyChanged += Memory_OnGoalEnemyChanged;
        }
        private void Memory_OnGoalEnemyChanged(BotOwner owner)
        {
            if (owner != null && owner.Memory != null && owner.Memory.GoalEnemy != null && owner.Memory.HaveEnemy)
            {
                if (owner.Memory.GoalEnemy.Person == (IPlayer)gameWorld.MainPlayer.InteractablePlayer && owner.Memory.GoalEnemy.HaveSeenPersonal && owner.Memory.GoalEnemy.IsVisible)
                {
                    timeSinceLastReplenish = 0f;
                }
            }
        }

        private void Mainplayer_BeingHitAction(DamageInfoStruct arg1, EBodyPart arg2, float arg3)
        {
            switch (arg1.DamageType)
            {
                case EDamageType.Btr:
                case EDamageType.Melee:
                case EDamageType.Bullet:
                case EDamageType.Explosion:
                case EDamageType.GrenadeFragment:
                case EDamageType.Sniper:
                    timeSinceLastReplenish = 0f;
                    break;
                default:
                    break;
            }
        }

        private async UniTask InitializeAllBotInfos(StartingBotConfig startingBotConfig, string maplocation, CancellationToken cancellationToken)
        {
            await UniTask.WhenAll(
                InitializeBotInfos(startingBotConfig, maplocation, "PMC", cancellationToken),
                InitializeBotInfos(startingBotConfig, maplocation, "SCAV", cancellationToken)
            );
        }

        private async UniTask InitializeBotInfos(StartingBotConfig startingBotConfig, string maplocation, string botType, CancellationToken cancellationToken)
        {
            botType = DefaultPluginVars.forceAllBotType.Value switch
            {
                "PMC" => "PMC",
                "SCAV" => "SCAV",
                _ => botType
            };

            string difficultySetting = botType == "PMC" ? DefaultPluginVars.botDifficultiesPMC.Value.ToLower() : DefaultPluginVars.botDifficultiesSCAV.Value.ToLower();
            maplocation = maplocation == "sandbox_high" ? "sandbox" : maplocation;
            var mapBotConfig = botType == "PMC" ? startingBotConfig.Maps[maplocation].PMC : startingBotConfig.Maps[maplocation].SCAV;
            var difficultiesForSetting = GetDifficultiesForSetting(difficultySetting);
            int maxBots = UnityEngine.Random.Range(mapBotConfig.MinCount, mapBotConfig.MaxCount + 1);
            maxBots = botType switch
            {
                "PMC" when maxBots > Initialization.PMCBotLimit => Initialization.PMCBotLimit,
                "SCAV" when maxBots > Initialization.SCAVBotLimit => Initialization.SCAVBotLimit,
                _ => maxBots
            };

            Logger.LogDebug($"Max starting bots for {botType}: {maxBots}");

            var spawnPointsDict = DonutComponent.GetSpawnPointsForZones(allMapsZoneConfig, maplocation, mapBotConfig.Zones);

            int totalBots = 0;
            var usedZones = botType == "PMC" ? usedZonesPMC : usedZonesSCAV;
            var random = new System.Random();

            while (totalBots < maxBots)
            {
                int groupSize = BotSpawn.DetermineMaxBotCount(botType.ToLower(), mapBotConfig.MinGroupSize, mapBotConfig.MaxGroupSize);
                groupSize = Math.Min(groupSize, maxBots - totalBots);

                var wildSpawnType = botType == "PMC" ? GetPMCWildSpawnType(WildSpawnType.pmcUSEC, WildSpawnType.pmcBEAR) : WildSpawnType.assault;
                var side = botType == "PMC" ? GetPMCSide(wildSpawnType, WildSpawnType.pmcUSEC, WildSpawnType.pmcBEAR) : EPlayerSide.Savage;

                var difficulty = difficultiesForSetting[UnityEngine.Random.Range(0, difficultiesForSetting.Count)];

                var zoneKeys = spawnPointsDict.Keys.OrderBy(_ => random.Next()).ToList();
                string selectedZone = zoneKeys.FirstOrDefault(z => !usedZones.Contains(z));

                if (selectedZone == null)
                {
                    usedZones.Clear();
                    selectedZone = zoneKeys.First();
                }

                var coordinates = spawnPointsDict[selectedZone].OrderBy(_ => random.Next()).ToList();
                usedZones.Add(selectedZone);

                var botInfo = new PrepBotInfo(wildSpawnType, difficulty, side, groupSize > 1, groupSize);
                await CreateBot(botInfo, botInfo.IsGroup, botInfo.GroupSize, cancellationToken);
                BotInfos.Add(botInfo);

                var botSpawnInfo = new BotSpawnInfo(wildSpawnType, groupSize, coordinates, difficulty, side, selectedZone);
                botSpawnInfos.Add(botSpawnInfo);

                totalBots += groupSize;
            }
        }

        private WildSpawnType GetPMCWildSpawnType(WildSpawnType sptUsec, WildSpawnType sptBear)
        {
            switch (DefaultPluginVars.pmcFaction.Value)
            {
                case "USEC":
                    return WildSpawnType.pmcUSEC;
                case "BEAR":
                    return WildSpawnType.pmcBEAR;
                default:
                    return BotSpawn.DeterminePMCFactionBasedOnRatio(sptUsec, sptBear);
            }
        }

        private EPlayerSide GetPMCSide(WildSpawnType wildSpawnType, WildSpawnType sptUsec, WildSpawnType sptBear)
        {
            switch (wildSpawnType)
            {
                case WildSpawnType.pmcUSEC:
                    return EPlayerSide.Usec;
                case WildSpawnType.pmcBEAR:
                    return EPlayerSide.Bear;
                default:
                    return EPlayerSide.Usec;
            }
        }

        private List<BotDifficulty> GetDifficultiesForSetting(string difficultySetting)
        {
            switch (difficultySetting)
            {
                case "asonline":
                    return new List<BotDifficulty> { BotDifficulty.easy, BotDifficulty.normal, BotDifficulty.hard };
                case "easy":
                    return new List<BotDifficulty> { BotDifficulty.easy };
                case "normal":
                    return new List<BotDifficulty> { BotDifficulty.normal };
                case "hard":
                    return new List<BotDifficulty> { BotDifficulty.hard };
                case "impossible":
                    return new List<BotDifficulty> { BotDifficulty.impossible };
                default:
                    Logger.LogError("Unsupported difficulty setting: " + difficultySetting);
                    return new List<BotDifficulty>();
            }
        }

        private void Update()
        {
            timeSinceLastReplenish += Time.deltaTime;
            if (timeSinceLastReplenish >= DefaultPluginVars.replenishInterval.Value && !isReplenishing)
            {
                timeSinceLastReplenish = 0f;
                ReplenishAllBots(this.GetCancellationTokenOnDestroy()).Forget();
            }
        }

        private async UniTask ReplenishAllBots(CancellationToken cancellationToken)
        {
            isReplenishing = true;

            var tasks = new List<UniTask>();
            var botsNeedingReplenishment = BotInfos.Where(NeedReplenishment).ToList();

            int singleBotsCount = 0;
            int groupBotsCount = 0;

            foreach (var botInfo in botsNeedingReplenishment)
            {
                if (botInfo.IsGroup && groupBotsCount < 1)
                {
#if DEBUG
                    Logger.LogWarning($"Replenishing group bot: {botInfo.SpawnType} {botInfo.Difficulty} {botInfo.Side} Count: {botInfo.GroupSize}");
#endif
                    tasks.Add(CreateBot(botInfo, true, botInfo.GroupSize, cancellationToken));
                    groupBotsCount++;
                }
                else if (!botInfo.IsGroup && singleBotsCount < 3)
                {
#if DEBUG
                        Logger.LogWarning($"Replenishing single bot: {botInfo.SpawnType} {botInfo.Difficulty} {botInfo.Side} Count: 1");
#endif
                    tasks.Add(CreateBot(botInfo, false, 1, cancellationToken));
                    singleBotsCount++;
                }

                if (singleBotsCount >= 3 && groupBotsCount >= 1)
                    break;
            }

            if (tasks.Count > 0)
            {
                await UniTask.WhenAll(tasks);
            }

            isReplenishing = false;
        }

        private static bool NeedReplenishment(PrepBotInfo botInfo)
        {
            return botInfo.Bots == null || botInfo.Bots.Profiles.Count == 0;
        }

        internal static async UniTask CreateBot(PrepBotInfo botInfo, bool isGroup, int groupSize, CancellationToken cancellationToken)
        {
#if DEBUG
            Logger.LogDebug($"Creating bot: Type={botInfo.SpawnType}, Difficulty={botInfo.Difficulty}, Side={botInfo.Side}, GroupSize={groupSize}");
#endif

            GClass652 botData = new GClass652(botInfo.Side, botInfo.SpawnType, botInfo.Difficulty, 0f, null);

            BotCreationDataClass bot = await BotCreationDataClass.Create(botData, botCreator, groupSize, botSpawnerClass);
            if (bot == null || bot.Profiles == null || !bot.Profiles.Any())
            {
#if DEBUG
                Logger.LogError($"Failed to create or properly initialize bot for {botInfo.SpawnType}");
#endif
                return;
            }

            botInfo.Bots = bot;
#if DEBUG
            Logger.LogDebug($"Bot created and assigned successfully: {bot.Profiles.Count} profiles loaded.");
#endif
        }

        public static BotCreationDataClass FindCachedBots(WildSpawnType spawnType, BotDifficulty difficulty, int targetCount)
        {
            if (DonutsBotPrep.BotInfos == null)
            {
                Logger.LogError("BotInfos is null");
                return null;
            }

            try
            {
                // Find the bot info that matches the spawn type and difficulty
                var botInfo = DonutsBotPrep.BotInfos.FirstOrDefault(b => b.SpawnType == spawnType && b.Difficulty == difficulty && b.Bots != null && b.Bots.Profiles.Count == targetCount);

                if (botInfo != null)
                {
                    return botInfo.Bots;
                }

                Logger.LogWarning($"No cached bots found for spawn type {spawnType}, difficulty {difficulty}, and target count {targetCount}");
                return null;
            }
            catch (Exception ex)
            {
                Logger.LogError($"Exception in FindCachedBots: {ex.Message}\n{ex.StackTrace}");
                return null;
            }
        }

        public static List<BotCreationDataClass> GetWildSpawnData(WildSpawnType spawnType, BotDifficulty botDifficulty)
        {
            return BotInfos
                .Where(b => b.SpawnType == spawnType && b.Difficulty == botDifficulty)
                .Select(b => b.Bots)
                .ToList();
        }

        internal static WildSpawnType? GetOriginalSpawnTypeForBot(BotOwner bot)
        {
            var originalProfile = OriginalBotSpawnTypes.First(profile => profile.Key == bot.Profile.Id);

            if (originalProfile.Key != null)
            {
#if DEBUG
                Logger.LogWarning("Found original profile for bot " + bot.Profile.Nickname + " as " + originalProfile.Value.ToString());
#endif
                return originalProfile.Value;
            }
            else
            {
#if DEBUG
                Logger.LogWarning("Could not find original profile for bot " + bot.Profile.Nickname);
#endif
                return null;
            }
        }

        private void OnDestroy()
        {
            if (botSpawnerClass != null)
            {
                botSpawnerClass.OnBotRemoved -= BotSpawnerClass_OnBotRemoved;
                botSpawnerClass.OnBotCreated -= BotSpawnerClass_OnBotCreated;
            }

            if (mainplayer != null)
            {
                mainplayer.BeingHitAction -= Mainplayer_BeingHitAction;
            }

            isReplenishing = false;
            timeSinceLastReplenish = 0;
            IsBotPreparationComplete = false;

            gameWorld = null;
            botCreator = null;
            botSpawnerClass = null;
            mainplayer = null;
            OriginalBotSpawnTypes = null;
            BotInfos = null;
            botSpawnInfos = null;

#if DEBUG
            Logger.LogWarning("DonutsBotPrep component cleaned up and disabled.");
#endif
        }
    }
}
