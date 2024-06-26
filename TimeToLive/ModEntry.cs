﻿using HarmonyLib;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using System.Reflection.Emit;
using System.Reflection;
using Microsoft.Xna.Framework;
using StardewValley.Network;
using RadioactiveGeodes;

namespace TimeToLive
{
    public class ModEntry : Mod
    {
        internal Config config;
        internal static ITranslationHelper i18n;

        internal static ModEntry instance;

        internal static string ForageSpawnDateKey;
        internal static Harmony harmony;

        public override void Entry(IModHelper helper)
        {
            i18n = helper.Translation;
            config = Helper.ReadConfig<Config>();
            string startingMessage = i18n.Get("TimeToLive.start");
            Monitor.Log(startingMessage, LogLevel.Trace);

            instance = this;
            ForageSpawnDateKey = $"{ModManifest.UniqueID}/ForageSpawnDate";
            harmony = new Harmony(ModManifest.UniqueID);
            harmony.PatchAll();
            helper.Events.World.ObjectListChanged += OnObjectListChanged;
            helper.Events.GameLoop.GameLaunched += OnLaunched;
        }

        public void OnObjectListChanged(object? sender, ObjectListChangedEventArgs e)
        {
            if (e.Added == null)
            {
                return;
            }
            foreach (KeyValuePair<Vector2, StardewValley.Object> kvp in e.Added)
            {
                // IsSpawnedObject covers seasonal forage and shells on the beach
                if (kvp.Value.IsSpawnedObject)
                {
                    kvp.Value.modData[ForageSpawnDateKey] = WorldDate.Now().TotalDays.ToString();
                    instance.Monitor.Log(i18n.Get("TimeToLive.debug.event.dated", new { kvp.Value.DisplayName }), config.loggingLevel);
                }
            }
        }

        public void OnLaunched(object? sender, GameLaunchedEventArgs e)
        {
            if (Helper.ModRegistry.IsLoaded("spacechase0.GenericModConfigMenu"))
            {
                var api = Helper.ModRegistry.GetApi<IGenericModConfigMenuApi>("spacechase0.GenericModConfigMenu");

                api?.Register(ModManifest, () => config = new Config(), () => Helper.WriteConfig(config));
                api?.AddNumberOption(ModManifest, () => config.lifespan, (int val) => config.lifespan = val, () => i18n.Get("TimeToLive.config.lifespan.name"), () => i18n.Get("TimeToLive.config.lifespan.description"), 1, 28);
            }
        }
    }

    [HarmonyPatch(typeof(GameLocation))]
    public static class GameLocationPatch
    {

        [HarmonyTranspiler]
        [HarmonyPatch(typeof(GameLocation), nameof(GameLocation.DayUpdate), new Type[] { typeof(int) })]
        public static IEnumerable<CodeInstruction> DayUpdate_Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            ModEntry.instance.Monitor.Log(ModEntry.i18n.Get("TimeToLive.debug.transpiler.start"), ModEntry.instance.config.loggingLevel);
            FieldInfo objectsField = AccessTools.Field(typeof(GameLocation), nameof(GameLocation.objects));
            FieldInfo numberOfSpawnedObjectsOnMapField = AccessTools.Field(typeof(GameLocation), nameof(GameLocation.numberOfSpawnedObjectsOnMap));
            MethodInfo CheckForageForRemovalMethod = AccessTools.Method(typeof(GameLocationPatch), nameof(CheckForageForRemoval));
            CodeMatcher matcher = new(instructions, generator);
            CodeMatch[] firstTarget = new CodeMatch[]
            {
                new(OpCodes.Ldsfld),
                new(OpCodes.Ldc_I4_7),
                new(OpCodes.Rem),
                new(OpCodes.Brtrue)
            };
            CodeMatch[] secondTarget = new CodeMatch[] {
                new(OpCodes.Ldarg_0),
                new(OpCodes.Ldfld, objectsField),
                new(OpCodes.Ldloca_S)
            };
            // find start of relevant section of code, this causes the code we're targetting to run every day
            matcher.MatchStartForward(firstTarget);
            // nix the mod 7 portion of the if statement
            matcher.RemoveInstructions(4);
            // Skip ahead to where the stage is set for our detour
            matcher.Advance(1).MatchStartForward(secondTarget);
            
            // skip to ldloca_s
            matcher.Advance(3);
            // Remove the instructions that remove the Object from the set
            matcher.RemoveInstructions(3);
            // Insert our detour function, making use of the GameLocation inserted earlier
            // and the KeyValuePair that we salvaged from the Key retrieval
            // theres a Dictionary underneath it in the stack too that I need to eat with the function
            // the dictionary is useless as it's a copy and not the reference, but the desired reference can be reached through the KVP's Object.Location.objects property
            matcher.InsertAndAdvance(new CodeInstruction(OpCodes.Call, typeof(GameLocationPatch).GetMethod(nameof(CheckForageForRemoval))));

            // conditional decrement, skips if a Forage was not removed by previous instruction
            Label condLabel = generator.DefineLabel();
            var instructionsToInsert = new List<CodeInstruction>
            {
                // check top of stack for results of Forage removal check
                new(OpCodes.Brfalse_S, condLabel),
                // if removed, reference the instance and retrieve numberOfSpawnedObjectsOnMapField
                new(OpCodes.Ldarg_0),
                new(OpCodes.Ldarg_0),
                new(OpCodes.Ldfld, numberOfSpawnedObjectsOnMapField),
                // add a 1 to the stack and subtract it from variable value
                new(OpCodes.Ldc_I4_1),
                new(OpCodes.Sub),
                // reference the instance and set variable to the stack's value
                new(OpCodes.Stfld, numberOfSpawnedObjectsOnMapField),
                 // jump target to keep this segment self-contained
                new(OpCodes.Nop),
            };
            // bind the jump label to the NOP instruction
            instructionsToInsert.Last().labels.Add(condLabel);
            // just insert since we need to go hunting for a different set of instructions now
            matcher.Insert(instructionsToInsert);

            // being able to chain this makes it cleaner, though my urge to line up arguments split across lines doesn't help at all
            matcher.MatchStartForward(new (OpCodes.Ldarg_0), new (OpCodes.Ldc_I4_0), new (OpCodes.Stfld, numberOfSpawnedObjectsOnMapField)).RemoveInstructions(3);

            if (matcher.IsInvalid)
            {
                ModEntry.instance.Monitor.Log(ModEntry.i18n.Get("TimeToLive.debug.transpiler.failed"), ModEntry.instance.config.loggingLevel);
                return instructions;
            }
            // all done!
            ModEntry.instance.Monitor.Log(ModEntry.i18n.Get("TimeToLive.debug.transpiler.success"), ModEntry.instance.config.loggingLevel);
            return matcher.InstructionEnumeration();
        }

        public static bool CheckForageForRemoval(OverlaidDictionary objects, ref KeyValuePair<Vector2, StardewValley.Object> forage)
        {
            // we can store and retrieve custom data from here, it just needs to be serializable
            // in this case, we're using a simple int

            var objectModData = forage.Value.modData;
            int lifespan = ModEntry.instance.config.lifespan;

            if (objectModData != null && forage.Value.modData.TryGetValue(ModEntry.ForageSpawnDateKey, out string forageSpawnDate))
            {
                int currentTotalDays = WorldDate.Now().TotalDays;
                int spawnTotalDays = int.Parse(forageSpawnDate);

                // Simple math, just checking if enough time has passed for the forage to "decay"
                // variables could get rolled into the conditional but that's not legible
                if ((currentTotalDays - spawnTotalDays) >= lifespan)
                {
                    ModEntry.instance.Monitor.Log(ModEntry.i18n.Get("TimeToLive.debug.detour.age", new { forage.Value.DisplayName }), ModEntry.instance.config.loggingLevel);
                    forage.Value.Location.objects.Remove(forage.Key);
                    return true;
                }
                else
                {
                    ModEntry.instance.Monitor.Log(ModEntry.i18n.Get("TimeToLive.debug.detour.skip", new { forage.Value.DisplayName, Time = (currentTotalDays - spawnTotalDays) }), ModEntry.instance.config.loggingLevel);
                    return false;
                }
            }
            else
            {
                ModEntry.instance.Monitor.Log(ModEntry.i18n.Get("TimeToLive.debug.detour.undated", new { forage.Value.DisplayName }), ModEntry.instance.config.loggingLevel);
                forage.Value.Location.objects.Remove(forage.Key);
                return true;
            }
        }
    }
}
