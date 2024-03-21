using StardewModdingAPI;

namespace TimeToLive
{
    internal class Config
    {
        public SButton debugKey { get; set; }
        public int lifespan { get; set; }

        public Config()
        {
            lifespan = 7;
        }
    }
}
