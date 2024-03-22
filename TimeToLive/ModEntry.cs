using HarmonyLib;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using System.Reflection.Emit;
using System.Reflection;
using Microsoft.Xna.Framework;
using System.Reflection.Metadata.Ecma335;
using StardewModdingAPI.Utilities;

namespace TimeToLive
{
    public class ModEntry : Mod
    {
        internal Config? config;
        internal ITranslationHelper i18n => Helper.Translation;

        internal static ModEntry? instance;

        internal static string? ForageSpawnDateKey;

        public override void Entry(IModHelper helper)
        {
            string startingMessage = i18n.Get("TimeToLive.start");
            Monitor.Log(startingMessage, LogLevel.Trace);

            config = helper.ReadConfig<Config>();
            instance = this;
            ForageSpawnDateKey = $"{instance.ModManifest.UniqueID}/ForageSpawnDate";
        }
    }

    [HarmonyPatch(typeof(GameLocation))]
    public static class GameLocationPatch
    {

        [HarmonyTranspiler]
        [HarmonyPatch("spawnObjects")]
        public static IEnumerable<CodeInstruction> spawnObjects_Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            MethodInfo SetSpawnDateMethod = AccessTools.Method(typeof(GameLocationPatch), "SetSpawnDate");
            var matcher = new CodeMatcher(instructions, generator);

            // forageObj.IsSpawnedObject = true;
            matcher.MatchStartForward(new CodeMatch(OpCodes.Ldloc_S, (short)18),
                                      new CodeMatch(OpCodes.Ldc_I4_1),
                                      new CodeMatch(OpCodes.Callvirt, AccessTools.PropertySetter(typeof(StardewValley.Object), "IsSpawnedObject")));

            if (matcher.IsInvalid) return instructions;

            // ldloc   18
            // callvirt  instance void GameLocationPatch::SetSpawnDate(StardewValley.Object)
            matcher.Insert(new CodeInstruction(OpCodes.Ldloc_S, matcher.Instruction.operand),
                           new CodeInstruction(OpCodes.Callvirt, SetSpawnDateMethod));
            return matcher.InstructionEnumeration();
        }

        public static void SetSpawnDate(StardewValley.Object forage)
        {
            forage.modData[ModEntry.ForageSpawnDateKey] = WorldDate.Now().TotalDays.ToString();
            ModEntry.instance?.Monitor.Log($"Assigned current date to spawned {forage.DisplayName}.", ModEntry.instance.config.loggingLevel);
        }

        [HarmonyTranspiler]
        [HarmonyPatch("DayUpdate", new Type[]
        {
            typeof(int)
        })]
        public static IEnumerable<CodeInstruction> DayUpdate_Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            FieldInfo objectsField = AccessTools.Field(typeof(GameLocation), "objects");
            FieldInfo numberOfSpawnedObjectsOnMapField = AccessTools.Field(typeof(GameLocation), "numberOfSpawnedObjectsOnMap");
            MethodInfo CheckForageForRemovalMethod = AccessTools.Method(typeof(GameLocationPatch), "CheckForageForRemoval", new Type[]
            {
                typeof(GameLocation),
                typeof(KeyValuePair<Vector2, StardewValley.Object>)
            });


            var matcher = new CodeMatcher(instructions, generator);
            // find start of relevant section of code
            matcher.MatchStartForward(new CodeMatch(OpCodes.Ldarg_0),
                                      new CodeMatch(OpCodes.Ldfld, objectsField),
                                      new CodeMatch(OpCodes.Ldloca_S));
            // insert a GameLocation reference to the stack
            matcher.InsertAndAdvance(new CodeInstruction(OpCodes.Ldarg_0));
            // skip the matched instructions
            matcher.Advance(3);
            // Remove the instructions that remove the Object from the set
            matcher.RemoveInstructions(3);
            // Insert our detour function, making use of the GameLocation inserted earlier
            // and the KeyValuePair that we salvaged from the Key retrieval
            matcher.InsertAndAdvance(new CodeInstruction(OpCodes.Callvirt, CheckForageForRemovalMethod));

            // conditional decrement, skips if a Forage was not removed by previous instruction
            Label condLabel = generator.DefineLabel();
            var instructionsToInsert = new List<CodeInstruction>
            {
                // check top of stack for results of Forage removal check
                new CodeInstruction(OpCodes.Brfalse_S, condLabel),
                // if removed, reference the instance and retrieve numberOfSpawnedObjectsOnMapField
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Ldfld, numberOfSpawnedObjectsOnMapField),
                // add a 1 to the stack and subtract it from variable value
                new CodeInstruction(OpCodes.Ldc_I4_1),
                new CodeInstruction(OpCodes.Sub),
                // reference the instance and set variable to the stack's value
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Stfld, numberOfSpawnedObjectsOnMapField),
                // jump target to keep this segment self-contained
                new CodeInstruction(OpCodes.Nop)
            };
            // bind the jump label to the NOP instruction
            instructionsToInsert.Last().labels.Add(condLabel);
            // just insert since we need to go hunting for a different set of instructions now
            matcher.Insert(instructionsToInsert);

            matcher.MatchStartForward(new CodeMatch(OpCodes.Ldarg_0),
                                      new CodeMatch(OpCodes.Ldc_I4_0),
                                      new CodeMatch(OpCodes.Stfld, numberOfSpawnedObjectsOnMapField)).RemoveInstructions(3);

            // all done!
            return matcher.InstructionEnumeration();
        }

        public static bool CheckForageForRemoval(GameLocation map, KeyValuePair<Vector2, StardewValley.Object> forage)
        {
            ModEntry.instance?.Monitor.Log($"Checking {forage.Value.DisplayName} for spawn date.", ModEntry.instance.config.loggingLevel);

            // we can store and retrieve custom data from here, it just needs to be serializable
            // in this case, we're using a simple int
            var objectModData = forage.Value.modData;
            int lifespan = ModEntry.instance?.config?.lifespan != null ? ModEntry.instance.config.lifespan : 7;
            if (objectModData != null && objectModData[ModEntry.ForageSpawnDateKey] != null)
            {
                int currentTotalDays = WorldDate.Now().TotalDays;
                int spawnTotalDays = int.Parse(objectModData[ModEntry.ForageSpawnDateKey]);

                // Simple math, just checking if enough time has passed for the forage to "decay"
                if ((currentTotalDays - spawnTotalDays) > lifespan)
                {
                    ModEntry.instance?.Monitor.Log($"Despawning {forage.Value.DisplayName} due to age.", ModEntry.instance.config.loggingLevel);
                    map.objects.Remove(forage.Key);
                    return true;
                }
                else
                {
                    ModEntry.instance?.Monitor.Log($"Skipping {forage.Value.DisplayName} as it has {(currentTotalDays - spawnTotalDays)} days left.", ModEntry.instance.config.loggingLevel);
                    return false;
                }
            }
            // if ModData == null or ModData[ModEntry.ForageSpawnDateKey] == null
            else
            {
                ModEntry.instance?.Monitor.Log($"Despawning {forage.Value.DisplayName} as it either has no ModData or spawn date.", ModEntry.instance.config.loggingLevel);
                map.objects.Remove(forage.Key);
                return true;
            }
        }
    }
}
