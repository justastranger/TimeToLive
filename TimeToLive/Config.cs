﻿using StardewModdingAPI;

namespace TimeToLive
{
    internal class Config
    {
        public int lifespan { get; set; }
        public LogLevel loggingLevel { get; set; }

        public Config()
        {
            lifespan = 7;
            loggingLevel = LogLevel.Trace;
        }
    }
}
