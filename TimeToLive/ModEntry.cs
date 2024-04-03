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
using StardewValley.Network;
using Newtonsoft.Json;

namespace TimeToLive
{
    public class ModEntry : Mod
    {
        internal Config config;
        internal ITranslationHelper i18n => Helper.Translation;

        internal static ModEntry instance;

        internal static string ForageSpawnDateKey;
        internal static Harmony harmony;

        public override void Entry(IModHelper helper)
        {
            string startingMessage = i18n.Get("TimeToLive.start");
            Monitor.Log(startingMessage, LogLevel.Trace);

            config = helper.ReadConfig<Config>();
            instance = this;
            ForageSpawnDateKey = $"{instance.ModManifest.UniqueID}/ForageSpawnDate";
            harmony = new Harmony(ModManifest.UniqueID);
            harmony.PatchAll();
            // ObjectListChanged
            helper.Events.World.ObjectListChanged += OnObjectListChanged;
        }

        public void OnObjectListChanged(object? sender, ObjectListChangedEventArgs e)
        {
            if (e.Added != null)
            {
                foreach (KeyValuePair<Vector2, StardewValley.Object> kvp in e.Added)
                {
                    if (kvp.Value.isForage() && kvp.Value.IsSpawnedObject)
                    {
                        kvp.Value.modData[ForageSpawnDateKey] = WorldDate.Now().TotalDays.ToString();
                        instance.Monitor.Log($"Assigned current date to spawned {kvp.Value.DisplayName}.", LogLevel.Trace);
                    }
                }
            }

        }
    }

    [HarmonyPatch(typeof(GameLocation))]
    public static class GameLocationPatch
    {

        [HarmonyTranspiler]
        [HarmonyPatch(typeof(GameLocation), nameof(GameLocation.DayUpdate), new Type[]
        {
            typeof(int)
        })]
        public static IEnumerable<CodeInstruction> DayUpdate_Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            ModEntry.instance.Monitor.Log("Patching GameLocation.DayUpdate", LogLevel.Trace);
            FieldInfo objectsField = AccessTools.Field(typeof(GameLocation), nameof(GameLocation.objects));
            FieldInfo numberOfSpawnedObjectsOnMapField = AccessTools.Field(typeof(GameLocation), nameof(GameLocation.numberOfSpawnedObjectsOnMap));
            MethodInfo CheckForageForRemovalMethod = AccessTools.Method(typeof(GameLocationPatch), nameof(GameLocationPatch.CheckForageForRemoval));
            // LocalBuilder newVar = generator.DeclareLocal(typeof(KeyValuePair<Vector2, StardewValley.Object>));
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
            matcher.RemoveInstructions(4);
            // ModEntry.instance.Monitor.Log("Match 1 made at index: " + matcher.Pos, LogLevel.Trace);
            // We don't need to match the same target twice anymore since the first target is unique to the function and comes right before this onee
            matcher.Advance(1).MatchStartForward(secondTarget);
            // ModEntry.instance.Monitor.Log("Match 2 made at index: " + matcher.Pos, LogLevel.Trace);


            if (matcher.IsInvalid)
            {
                ModEntry.instance.Monitor.Log("Patching GameLocation.spawnObjects failed.", LogLevel.Error);
                return instructions;
            }

            // insert a GameLocation reference to the stack
            //matcher.InsertAndAdvance(new CodeInstruction(OpCodes.Ldarg_0));
            //matcher.InsertAndAdvance(new CodeInstruction(OpCodes.Ldobj, typeof(GameLocation)));
            // skip to ldloca_s
            matcher.Advance(3);
            // convert reference produced by ldloca.s to actual object of the desired type
            //matcher.InsertAndAdvance(new CodeInstruction(OpCodes.Ldobj, typeof(KeyValuePair<Vector2, StardewValley.Object>)));
            // Remove the instructions that remove the Object from the set
            matcher.RemoveInstructions(3);
            // Insert our detour function, making use of the GameLocation inserted earlier
            // and the KeyValuePair that we salvaged from the Key retrieval
            // theres a variable in the middle of the stack
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
            matcher.MatchStartForward(new (OpCodes.Ldarg_0),
                                      new (OpCodes.Ldc_I4_0),
                                      new (OpCodes.Stfld, numberOfSpawnedObjectsOnMapField)).RemoveInstructions(3);

            if (matcher.IsInvalid)
            {
                ModEntry.instance.Monitor.Log("Patching GameLocation.spawnObjects failed.", LogLevel.Error);
                return instructions;
            }

            List<string> insns = new();
            foreach (CodeInstruction pair in matcher.InstructionEnumeration())
            {
                string operand = pair.operand != null ? pair.operand.GetType() + ": " + pair.operand.ToString() : "";
                insns.Add(pair.opcode.Name + " " + operand);
            }
            ModEntry.instance.Monitor.Log(Newtonsoft.Json.JsonConvert.SerializeObject(insns));

            // all done!
            ModEntry.instance.Monitor.Log("Patched GameLocation.DayUpdate.", LogLevel.Trace);
            return matcher.InstructionEnumeration();
        }

        public static void TagInLog()
        {
            ModEntry.instance.Monitor.Log("Emitting tag into log from IL code.", LogLevel.Error);
        }

        public static bool CheckForageForRemoval(OverlaidDictionary objects, ref KeyValuePair<Vector2, StardewValley.Object> forage)
        {
            // ModEntry.instance.Monitor.Log("CheckForageForRemoval: Detour reached.", LogLevel.Error);

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
                    ModEntry.instance.Monitor.Log($"Despawning {forage.Value.DisplayName} due to age.", LogLevel.Trace);
                    forage.Value.Location.objects.Remove(forage.Key);
                    return true;
                }
                else
                {
                    ModEntry.instance.Monitor.Log($"Skipping {forage.Value.DisplayName} as it has {(currentTotalDays - spawnTotalDays)} days left.", LogLevel.Trace);
                    return false;
                }
            }
            else
            {
                ModEntry.instance.Monitor.Log($"Despawning {forage.Value.DisplayName} as it either has no ModData or spawn date. It was most likely spawned before this mod was installed.", LogLevel.Trace);
                forage.Value.Location.objects.Remove(forage.Key);
                return true;
            }
        }
    }
}
