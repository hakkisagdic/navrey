// SPDX-License-Identifier: BSD-2-Clause

using System.Linq;
using ClassicUO.Agent.Modules;

namespace ClassicUO.Agent
{
    internal sealed partial class CommandEngine
    {
        private const string HUD_USAGE = "hud [hide|show] [windows|overlay|cursor|player|all]...";

        private void RegisterModules()
        {
            Register("hud", HUD_USAGE,
                "Hide or show parts of the screen for clean video frames (`hud hide` alone: windows and cursor)", ctx =>
            {
                string verb = ctx.Arg(0)?.ToLowerInvariant();

                if (verb == null)
                {
                    ctx.Print($"Hidden: {ctx.Game(_ => Hud.Describe(Hud.Hidden))}");

                    return;
                }

                if (verb != "hide" && verb != "show")
                {
                    ctx.Warn($"usage: {HUD_USAGE}");

                    return;
                }

                if (!Hud.TryParse(ctx.Args.Skip(2), out Hud.Parts parts, out string unknown))
                {
                    ctx.Warn($"unknown part '{unknown}' - windows, overlay, cursor, player or all");

                    return;
                }

                bool hide = verb == "hide";

                if (parts == Hud.Parts.None)
                {
                    parts = hide ? Hud.DEFAULT_PARTS : Hud.Parts.All;
                }

                string now = ctx.Game(_ =>
                {
                    if (hide)
                    {
                        Hud.Hide(parts);
                    }
                    else
                    {
                        Hud.Show(parts);
                    }

                    return Hud.Describe(Hud.Hidden);
                });

                ctx.Print($"{(hide ? "Hid" : "Showed")} {Hud.Describe(parts)} - hidden now: {now}");
            });
        }
    }
}
