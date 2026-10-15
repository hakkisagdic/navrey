// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Linq;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;

namespace ClassicUO.Agent
{
    internal sealed partial class CommandEngine
    {
        private void RegisterLocks()
        {
            // The arrows beside each skill and stat in the game's own gumps: up lets it gain, down
            // lets the server lower it to make room under the cap, locked holds it. Set the local
            // value first and then ask the server, exactly as those gumps do; the server's skill
            // update echoes the lock back, so `skills` and the state file show what it accepted.
            Register("skilllock", "skilllock <skill name or id> <up|down|locked>",
                "Set a skill's lock: up (may gain), down (may drop) or locked", ctx =>
            {
                if (!ctx.RequireInGame())
                {
                    return;
                }

                if (ctx.ArgCount < 2 || !TryParseLock(ctx.Args[^1], out Lock state))
                {
                    ctx.Warn("usage: skilllock <skill name or id> <up|down|locked>");

                    return;
                }

                string query = string.Join(" ", ctx.Args.Skip(1).Take(ctx.ArgCount - 1));

                (string error, string done) = ctx.Game(w =>
                {
                    Skill skill = FindSkill(w.Player, query, out string why);

                    if (skill == null)
                    {
                        return (why, null);
                    }

                    skill.Lock = state;
                    GameActions.ChangeSkillLockStatus((ushort)skill.Index, (byte)state);

                    return ((string)null, $"{skill.Name} lock set to {LockName(state)}");
                });

                if (error != null)
                {
                    ctx.Fail(error);

                    return;
                }

                ctx.Print(done);
            });

            Register("statlock", "statlock <str|dex|int> <up|down|locked>",
                "Set a stat's lock: up (may gain), down (may drop) or locked", ctx =>
            {
                if (!ctx.RequireInGame())
                {
                    return;
                }

                int stat = ctx.Arg(0)?.ToLowerInvariant() switch
                {
                    "str" or "strength" => 0,
                    "dex" or "dexterity" => 1,
                    "int" or "intelligence" => 2,
                    _ => -1
                };

                if (stat < 0 || ctx.ArgCount != 2 || !TryParseLock(ctx.Arg(1), out Lock state))
                {
                    ctx.Warn("usage: statlock <str|dex|int> <up|down|locked>");

                    return;
                }

                ctx.Game(w =>
                {
                    PlayerMobile p = w.Player;

                    switch (stat)
                    {
                        case 0: p.StrLock = state; break;
                        case 1: p.DexLock = state; break;
                        default: p.IntLock = state; break;
                    }

                    GameActions.ChangeStatLock((byte)stat, state);
                });

                ctx.Print($"{StatNames[stat]} lock set to {LockName(state)}");
            });
        }

        private static readonly string[] StatNames = { "STR", "DEX", "INT" };

        private static bool TryParseLock(string text, out Lock state)
        {
            switch (text?.ToLowerInvariant())
            {
                case "up":
                    state = Lock.Up;

                    return true;

                case "down":
                case "dn":
                    state = Lock.Down;

                    return true;

                case "locked":
                case "lock":
                    state = Lock.Locked;

                    return true;

                default:
                    state = Lock.Up;

                    return false;
            }
        }

        /// <summary>The lock as `skills` prints it.</summary>
        private static string LockName(Lock state) => state switch
        {
            Lock.Up => "up",
            Lock.Down => "dn",
            _ => "locked"
        };

        /// <summary>
        /// A skill by id, by its full name, or by the one name that starts with or contains the
        /// query - spaces and case ignored, so "animal taming", "animaltaming" and "taming" all
        /// work. Game thread.
        /// </summary>
        private static Skill FindSkill(PlayerMobile player, string query, out string why)
        {
            why = null;
            Skill[] skills = player.Skills;

            if (int.TryParse(query, out int id))
            {
                if (id >= 0 && id < skills.Length)
                {
                    return skills[id];
                }

                why = $"no skill with id {id} (0-{skills.Length - 1})";

                return null;
            }

            static string Key(string s) => s.Replace(" ", string.Empty).ToLowerInvariant();

            string wanted = Key(query);
            Skill exact = skills.FirstOrDefault(s => Key(s.Name) == wanted);

            if (exact != null)
            {
                return exact;
            }

            Skill[] matches = skills.Where(s => Key(s.Name).StartsWith(wanted, StringComparison.Ordinal)).ToArray();

            if (matches.Length == 0)
            {
                matches = skills.Where(s => Key(s.Name).Contains(wanted, StringComparison.Ordinal)).ToArray();
            }

            if (matches.Length == 1)
            {
                return matches[0];
            }

            why = matches.Length == 0
                ? $"no skill called '{query}'"
                : $"'{query}' could be {string.Join(", ", matches.Select(s => s.Name))} - say which";

            return null;
        }
    }
}
