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
            var oldInstructions = new List<CodeInstruction>(instructions);
            var newInstructions = new List<CodeInstruction>(instructions);

            FieldInfo objectsField = AccessTools.Field(typeof(GameLocation), "objects");
            FieldInfo numberOfSpawnedObjectsOnMapField = AccessTools.Field(typeof(GameLocation), "numberOfSpawnedObjectsOnMap");
            MethodInfo CheckForageForRemovalMethod = AccessTools.Method(typeof(DayUpdateForagePatch), "CheckForageForRemoval", new Type[]
            {
                typeof(GameLocation),
                typeof(KeyValuePair<Vector2, StardewValley.Object>)
            });

            for (int i = 0; i < oldInstructions.Count - 1; i++)
            {
                if (oldInstructions[i-2].opcode == OpCodes.Ldarg_0
                    && oldInstructions[i-1].opcode == OpCodes.Ldfld
                    && (FieldInfo)oldInstructions[i-1].operand == objectsField
                    && oldInstructions[i].opcode == OpCodes.Ldloca_S)
                {

                    // remove the next 3 instructions
                    newInstructions.RemoveRange(i+1, 3);

                    // insert ldarg_0 at i-2, duplicating the existing one

                    newInstructions.Insert(i-2, new CodeInstruction(OpCodes.Ldarg_0));

                    // increment i manually to account for the shift

                    i++;

                    // insert a callvirt to our detour

                    newInstructions.Insert(i+1, new CodeInstruction(OpCodes.Callvirt, CheckForageForRemovalMethod));

                    // when that returns, we need to add the conditional decrementing

                    Label condLabel = generator.DefineLabel();

                    var instructionsToInsert = new List<CodeInstruction>
                    {
                        new CodeInstruction(OpCodes.Brfalse_S, condLabel),
                        new CodeInstruction(OpCodes.Ldarg_0),
                        new CodeInstruction(OpCodes.Ldfld, numberOfSpawnedObjectsOnMapField),
                        new CodeInstruction(OpCodes.Ldc_I4_1),
                        new CodeInstruction(OpCodes.Sub),
                        new CodeInstruction(OpCodes.Ldarg_0),
                        new CodeInstruction(OpCodes.Stfld, numberOfSpawnedObjectsOnMapField),
                        new CodeInstruction(OpCodes.Nop)
                    };
                    instructionsToInsert.Last().labels.Add(condLabel);

                    // insert the above instructions into the spot the removal created
                    // do not touch anything past i+2 until we get to the next for loop
                    // because the indexes are all mismatched and we only accounted for the one insertion
                    newInstructions.InsertRange(i+2, instructionsToInsert);

                    break;
                }
                if (i == oldInstructions.Count - 1)
                {
                    ModEntry.instance?.Monitor.Log("Transpiler failed to find Forage Removal target.", LogLevel.Error);
                    return oldInstructions;
                }
            }

            int removalIndex = -1;
            // Time to move past the loop we were modifying
            // and remove the code that zeroes out numberOfSpawnedObjectsOnMap
            for (int i = 0; i < newInstructions.Count - 1; i++)
            {
                // since modifying an iterable collection is a bad idea mid-iteration
                // we're just gonna grab the index that we find the instructions at
                if (newInstructions[i].opcode == OpCodes.Ldarg_0
                    && newInstructions[i+1].opcode == OpCodes.Ldc_I4_0
                    && newInstructions[i+2].opcode == OpCodes.Stfld
                    && (FieldInfo)newInstructions[i+2].operand == numberOfSpawnedObjectsOnMapField)
                {
                    removalIndex = i;
                    break;
                }
            }
            // if we find it
            if (removalIndex > 0)
            {
                // remove the three instructions involved
                newInstructions.RemoveRange(removalIndex, 3);
            }
            else
            {
                ModEntry.instance?.Monitor.Log("Transpiler failed to find and remove numberOfSpawnedObjectsOnMap clearing instructions.", LogLevel.Error);
                return oldInstructions;
            }

            // all done!
            return newInstructions;
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
