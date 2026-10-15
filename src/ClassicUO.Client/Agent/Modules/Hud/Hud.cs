// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using ClassicUO.Game.UI.Gumps;
using static SDL3.SDL;

namespace ClassicUO.Agent.Modules
{
    /// <summary>
    /// Hides parts of the screen for clean video frames - the `hud` command. Nothing changes until
    /// it is asked to hide something, and `hud show` puts back exactly what it hid, never a window
    /// the game or the player had closed or hidden on their own.
    ///
    ///   windows  every window (gump) except the world view itself and the overlay; a window that
    ///            opens while they are hidden is hidden too
    ///   overlay  the names and hit-point bars of the `overlay` feature
    ///   cursor   the mouse pointer (drawn by the system, so it is never in a recorded frame - this
    ///            is for screen captures from outside the client)
    ///   player   the player's own character in the world view
    /// </summary>
    internal static class Hud
    {
        [Flags]
        public enum Parts
        {
            None = 0,
            Windows = 1,
            Overlay = 2,
            Cursor = 4,
            Player = 8,
            All = Windows | Overlay | Cursor | Player
        }

        /// <summary>What `hud hide` with no parts named hides: everything but the world and the overlay's labels.</summary>
        public const Parts DEFAULT_PARTS = Parts.Windows | Parts.Cursor;

        private static readonly List<Gump> _hiddenGumps = new();

        /// <summary>What is hidden right now.</summary>
        public static Parts Hidden { get; private set; }

        /// <summary>Hides <paramref name="parts"/> and keeps them hidden. Game thread.</summary>
        public static void Hide(Parts parts)
        {
            Hidden |= parts;

            if ((parts & Parts.Cursor) != 0)
            {
                SDL_HideCursor();
            }

            if ((parts & Parts.Player) != 0)
            {
                PlayerMobile.HideFromView = true;
            }

            Pump();
        }

        /// <summary>Shows <paramref name="parts"/> again, restoring only what <see cref="Hide"/> hid. Game thread.</summary>
        public static void Show(Parts parts)
        {
            Hidden &= ~parts;

            if ((parts & Parts.Cursor) != 0)
            {
                SDL_ShowCursor();
            }

            if ((parts & Parts.Player) != 0)
            {
                PlayerMobile.HideFromView = false;
            }

            for (int i = _hiddenGumps.Count - 1; i >= 0; i--)
            {
                Gump gump = _hiddenGumps[i];

                if (gump.IsDisposed)
                {
                    _hiddenGumps.RemoveAt(i);
                }
                else if ((PartOf(gump) & parts) != 0)
                {
                    gump.IsVisible = true;
                    _hiddenGumps.RemoveAt(i);
                }
            }
        }

        /// <summary>Hides windows that opened since the last frame. Game thread, every frame; free while nothing is hidden.</summary>
        public static void Pump()
        {
            if ((Hidden & (Parts.Windows | Parts.Overlay)) == 0)
            {
                return;
            }

            foreach (Gump gump in UIManager.Gumps)
            {
                if (gump.IsDisposed || !gump.IsVisible || gump is WorldViewportGump || (PartOf(gump) & Hidden) == 0)
                {
                    continue;
                }

                gump.IsVisible = false;
                _hiddenGumps.Add(gump);
            }
        }

        /// <summary>Reads part names - windows, overlay, cursor, player, all - into flags.</summary>
        public static bool TryParse(IEnumerable<string> words, out Parts parts, out string unknown)
        {
            parts = Parts.None;
            unknown = null;

            foreach (string word in words)
            {
                Parts part = word.ToLowerInvariant() switch
                {
                    "windows" or "window" or "gumps" => Parts.Windows,
                    "overlay" => Parts.Overlay,
                    "cursor" or "mouse" => Parts.Cursor,
                    "player" or "me" => Parts.Player,
                    "all" => Parts.All,
                    _ => Parts.None
                };

                if (part == Parts.None)
                {
                    unknown = word;

                    return false;
                }

                parts |= part;
            }

            return true;
        }

        public static string Describe(Parts parts) =>
            parts == Parts.None ? "nothing" : parts.ToString().ToLowerInvariant();

        private static Parts PartOf(Gump gump) => gump is MobileOverlayGump ? Parts.Overlay : Parts.Windows;
    }
}
