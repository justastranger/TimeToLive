using HarmonyLib;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using System.Reflection.Emit;
using System.Reflection;
using Microsoft.Xna.Framework;

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
            string startingMessage = i18n.Get("TimeToLive.start", new { mod = helper.ModRegistry.ModID, folder = helper.DirectoryPath });
            Monitor.Log(startingMessage, LogLevel.Trace);

            config = helper.ReadConfig<Config>();
            instance = this;
            ForageSpawnDateKey = $"{instance.ModManifest.UniqueID}/ForageSpawnDate";
        }
    }

    [HarmonyPatch(typeof(GameLocation), "DayUpdate", new Type[]
    {
        typeof(int)
    })]
    public static class DayUpdateForagePatch
    {



        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator, MethodBase original)
        {
            FieldInfo objectsField = AccessTools.Field(typeof(GameLocation), "objects");
            FieldInfo numberOfSpawnedObjectsOnMapField = AccessTools.Field(typeof(GameLocation), "numberOfSpawnedObjectsOnMap");
            MethodInfo CheckForageForRemovalMethod = AccessTools.Method(typeof(DayUpdateForagePatch), "CheckForageForRemoval", new Type[]
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
                                      new CodeMatch(OpCodes.Stfld, numberOfSpawnedObjectsOnMapField));
            matcher.RemoveInstructions(3);

            // all done!
            return matcher.InstructionEnumeration();
        }

        public static bool CheckForageForRemoval(GameLocation map, KeyValuePair<Vector2, StardewValley.Object> forage)
        {
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
                    map.objects.Remove(forage.Key);
                    return true;
                }
                else return false;
            }
            else
            {
                map.objects.Remove(forage.Key);
                return true;
            }
        }
    }
}
