// SPDX-License-Identifier: BSD-2-Clause

using System.Collections.Generic;
using ClassicUO.Configuration;
using ClassicUO.Game;

namespace ClassicUO.Agent.Modules
{
    /// <summary>
    /// The opt-in features switched on in settings.json's `features` block. They start with the
    /// agent host and live under Agent/Modules, one folder each; a feature that is off registers
    /// nothing - no hook, no per-frame work, no log line - so with every switch off the client runs
    /// exactly as it did before modules existed.
    ///
    /// The switches are read once, when the agent starts. The client never writes settings.json,
    /// so switching a feature means editing the file and restarting the client.
    /// </summary>
    internal static class FeatureModules
    {
        /// <summary>The switches this session runs with. All off until <see cref="Start"/>.</summary>
        public static FeatureSwitches Switches { get; private set; } = new FeatureSwitches();

        /// <summary>Reads the switches and starts what they enable. Called once from the agent host.</summary>
        public static void Start()
        {
            Switches = Settings.GlobalSettings?.Features ?? new FeatureSwitches();

            var on = new List<string>();

            if (Switches.ActionLog)
            {
                on.Add("action_log");
            }

            if (Switches.WorldPathfinder)
            {
                on.Add("world_pathfinder");
            }

            if (Switches.Overlay)
            {
                on.Add("overlay");
            }

            if (on.Count > 0)
            {
                Output.Raw($"=== features on: {string.Join(", ", on)} ===");
            }
        }

        /// <summary>Per-frame work of whatever is on. Game thread, from AgentHost.PumpGameThread.</summary>
        public static void Pump(World world)
        {
        }

        /// <summary>Stops everything that runs, flushing what has files open. Called when the agent host shuts down.</summary>
        public static void Stop()
        {
        }
    }
}
