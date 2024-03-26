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
            // GameLocation e.Location
            // IEnumerable<KeyValuePair<Vector2, Object>> e.Added

            if (e.Added != null)
            {
                foreach (KeyValuePair<Vector2, StardewValley.Object> kvp in e.Added)
                {
                    if (kvp.Value.isForage())
                    {
                        kvp.Value.modData[ForageSpawnDateKey] = WorldDate.Now().TotalDays.ToString();
                        instance.Monitor.Log($"Assigned current date to spawned {kvp.Value.DisplayName}.");
                    }
                }
            }

        }
    }

    [HarmonyPatch(typeof(GameLocation))]
    public static class GameLocationPatch
    {

        // hopefully obsolete, haven't been able to test it whilst transfixed by the eldritch error
        //[HarmonyTranspiler]
        //[HarmonyPatch(typeof(GameLocation), nameof(GameLocation.spawnObjects))]
        //public static IEnumerable<CodeInstruction> spawnObjects_Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        //{
        //    ModEntry.instance.Monitor.Log("Patching GameLocation.spawnObjects");
        //    MethodInfo SetSpawnDateMethod = AccessTools.Method(typeof(GameLocationPatch), nameof(GameLocationPatch.SetSpawnDate));
        //    var matcher = new CodeMatcher(instructions, generator);

        //    // forageObj.IsSpawnedObject = true;
        //    matcher.MatchStartForward(new CodeMatch(OpCodes.Ldloc_S),
        //                              new CodeMatch(OpCodes.Ldc_I4_1),
        //                              new CodeMatch(OpCodes.Callvirt, AccessTools.PropertySetter(typeof(StardewValley.Object), nameof(StardewValley.Object.IsSpawnedObject))));

        //    if (matcher.IsInvalid)
        //    {
        //        ModEntry.instance.Monitor.Log("Patching GameLocation.spawnObjects failed.");
        //        return instructions;
        //    }

        //    // Since the matcher is pointed at the first instruction that references the local variable we want
        //    // We're able to directly use the value of the instruction's operand to duplicate the reference
        //    // and then pass that to our code so that we can attach our data to it
        //    matcher.Insert(new CodeInstruction(OpCodes.Ldloc_S, matcher.Instruction.operand),
        //                   new CodeInstruction(OpCodes.Call, SetSpawnDateMethod));
        //    ModEntry.instance.Monitor.Log("Patched GameLocation.spawnObjects");
        //    return matcher.InstructionEnumeration();
        //}

        //public static void SetSpawnDate(StardewValley.Object forage)
        //{
        //    forage.modData[ModEntry.ForageSpawnDateKey] = WorldDate.Now().TotalDays.ToString();
        //    ModEntry.instance.Monitor.Log($"Assigned current date to spawned {forage.DisplayName}.");
        //}

        [HarmonyTranspiler]
        [HarmonyPatch(typeof(GameLocation), nameof(GameLocation.DayUpdate), new Type[]
        {
            typeof(int)
        })]
        public static IEnumerable<CodeInstruction> DayUpdate_Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            ModEntry.instance.Monitor.Log("Patching GameLocation.DayUpdate");
            FieldInfo objectsField = AccessTools.Field(typeof(GameLocation), nameof(GameLocation.objects));
            FieldInfo numberOfSpawnedObjectsOnMapField = AccessTools.Field(typeof(GameLocation), nameof(GameLocation.numberOfSpawnedObjectsOnMap));
            MethodInfo CheckForageForRemovalMethod = AccessTools.Method(typeof(GameLocationPatch), nameof(GameLocationPatch.CheckForageForRemoval));

            
            var matcher = new CodeMatcher(instructions, generator);
            // temp code dump
            List<string> insns = new();
            foreach (CodeInstruction pair in matcher.InstructionEnumeration())
            {
                string operand = pair.operand != null ? pair.operand.GetType() + ": " + pair.operand.ToString() : "";
                insns.Add(pair.opcode.Name + " " + operand);
            }
            ModEntry.instance.Monitor.Log(Newtonsoft.Json.JsonConvert.SerializeObject(insns));

            CodeMatch[] targetCode = new CodeMatch[] {
                new(OpCodes.Ldarg_0),
                new(OpCodes.Ldfld, objectsField),
                new(OpCodes.Ldloca_S)
            };
            // find start of relevant section of code
            matcher.MatchStartForward(targetCode);
            ModEntry.instance.Monitor.Log("Match 1 made at index: " + matcher.Pos);
            // twice because there's two copies of this particular fragment in wildly different spots and refining doesn't work
            matcher.Advance(1).MatchStartForward(targetCode);
            ModEntry.instance.Monitor.Log("Match 2 made at index: " + matcher.Pos);


            if (matcher.IsInvalid)
            {
                ModEntry.instance.Monitor.Log("Patching GameLocation.spawnObjects failed.");
                return instructions;
            }

            // insert a GameLocation reference to the stack
            matcher.InsertAndAdvance(new CodeInstruction(OpCodes.Ldarg_0));
            // skip the matched instructions
            matcher.Advance(3);
            // Remove the instructions that remove the Object from the set
            matcher.RemoveInstructions(3);
            // Insert our detour function, making use of the GameLocation inserted earlier
            // and the KeyValuePair that we salvaged from the Key retrieval
            // matcher.InsertAndAdvance(new CodeInstruction(OpCodes.Call, CheckForageForRemovalMethod));
            // matcher.InsertAndAdvance(new CodeInstruction(OpCodes.Callvirt, CheckForageForRemovalMethod));
            matcher.InsertAndAdvance(new CodeInstruction(OpCodes.Call, typeof(GameLocationPatch).GetMethod(nameof(GameLocationPatch.CheckForageForRemoval), BindingFlags.Static | BindingFlags.Public)));

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

            // being able to chain this makes it cleaner, though my urge to line up arguments split across lines doesn't help at all
            matcher.MatchStartForward(new (OpCodes.Ldarg_0),
                                      new (OpCodes.Ldc_I4_0),
                                      new (OpCodes.Stfld, numberOfSpawnedObjectsOnMapField)).RemoveInstructions(3);

            if (matcher.IsInvalid)
            {
                ModEntry.instance.Monitor.Log("Patching GameLocation.spawnObjects failed.");
                return instructions;
            }

            // all done!
            ModEntry.instance.Monitor.Log("Patched GameLocation.DayUpdate, dumping new instructions.");

            insns = new();
            foreach (CodeInstruction pair in matcher.InstructionEnumeration())
            {
                string operand = pair.operand != null ? pair.operand.GetType() + ": " + pair.operand.ToString() : "";
                insns.Add(pair.opcode.Name + " " + operand);
            }
            ModEntry.instance.Monitor.Log(Newtonsoft.Json.JsonConvert.SerializeObject(insns));

            return matcher.InstructionEnumeration();
        }

        public static bool CheckForageForRemoval(GameLocation map, KeyValuePair<Vector2, StardewValley.Object> forage)
        {
            ModEntry.instance.Monitor.Log($"Checking {forage.Value.DisplayName} for spawn date.");

            // we can store and retrieve custom data from here, it just needs to be serializable
            // in this case, we're using a simple int
            var objectModData = forage.Value.modData;
            int lifespan = ModEntry.instance.config.lifespan;
            if (objectModData != null && objectModData[ModEntry.ForageSpawnDateKey] != null)
            {
                int currentTotalDays = WorldDate.Now().TotalDays;
                int spawnTotalDays = int.Parse(objectModData[ModEntry.ForageSpawnDateKey]);

                // Simple math, just checking if enough time has passed for the forage to "decay"
                // variables could get rolled into the conditional but that's not legible
                if ((currentTotalDays - spawnTotalDays) > lifespan)
                {
                    ModEntry.instance.Monitor.Log($"Despawning {forage.Value.DisplayName} due to age.");
                    map.objects.Remove(forage.Key);
                    return true;
                }
                else
                {
                    ModEntry.instance.Monitor.Log($"Skipping {forage.Value.DisplayName} as it has {(currentTotalDays - spawnTotalDays)} days left.");
                    return false;
                }
            }
            // if ModData == null or ModData[ModEntry.ForageSpawnDateKey] == null
            else
            {
                ModEntry.instance.Monitor.Log($"Despawning {forage.Value.DisplayName} as it either has no ModData or spawn date.");
                map.objects.Remove(forage.Key);
                return true;
            }
        }
    }
}
