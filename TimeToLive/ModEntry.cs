using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewModdingAPI.Events;

namespace TimeToLive
{
    public class ModEntry : Mod
    {
        internal Config config;
        internal ITranslationHelper i18n => Helper.Translation;

        public override void Entry(IModHelper helper)
        {
            string startingMessage = i18n.Get("TimeToLive.start", new { mod = helper.ModRegistry.ModID, folder = helper.DirectoryPath });
            Monitor.Log(startingMessage, LogLevel.Trace);

            config = helper.ReadConfig<Config>();

        }
    }

    [HarmonyPatch(typeof(NPC), "draw", new Type[]
    {
        typeof(SpriteBatch),
        typeof(float)
    })]
    public static class patch
    {

    }
}
