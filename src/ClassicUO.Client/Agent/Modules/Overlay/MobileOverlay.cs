// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using ClassicUO.Game.Scenes;
using ClassicUO.Game.UI.Controls;
using ClassicUO.Game.UI.Gumps;
using ClassicUO.Renderer;
using Microsoft.Xna.Framework;

namespace ClassicUO.Agent.Modules
{
    /// <summary>
    /// The `overlay` feature: every mobile on screen gets its name, in its notoriety colour, and a
    /// small hit-point bar over its head - for demo videos and for a human watching an agent play.
    /// It is our own drawing, independent of the per-character name and health-line options, so
    /// it looks the same for every character and leaves their profiles alone.
    ///
    /// It is one window spanning the screen that takes no input, added when the player enters the
    /// world; the game clears it on logout and it is added again on the next login.
    /// </summary>
    internal static class MobileOverlay
    {
        private static MobileOverlayGump _gump;

        /// <summary>Keeps the overlay window present while in the world. Game thread, every frame.</summary>
        public static void Pump(World world)
        {
            if (world?.Player == null || !world.InGame || Client.Game.Scene is not GameScene)
            {
                return;
            }

            if (_gump == null || _gump.IsDisposed)
            {
                _gump = new MobileOverlayGump(world);
                UIManager.Add(_gump);
            }
        }
    }

    /// <summary>The overlay's window. See <see cref="MobileOverlay"/>.</summary>
    internal sealed class MobileOverlayGump : Gump
    {
        private const int BAR_WIDTH = 30;
        private const int BAR_HEIGHT = 4;
        private const byte NAME_FONT = 1;

        /// <summary>The name text drawn for each mobile, rebuilt only when its name or colour changes.</summary>
        private readonly Dictionary<uint, (string name, ushort hue, RenderedText text)> _names = new();

        private readonly HashSet<uint> _seen = new();
        private readonly List<uint> _stale = new();
        private readonly List<(Rectangle rect, Color fill, int filled)> _bars = new();
        private readonly Func<UltimaBatcher2D, bool> _drawBars;
        private float _barDepth;

        public MobileOverlayGump(World world) : base(world, 0, 0)
        {
            CanMove = false;
            AcceptMouseInput = false;
            CanCloseWithRightClick = false;
            CanCloseWithEsc = false;
            LayerOrder = UILayer.Under;

            _drawBars = DrawBars;
        }

        public override bool Contains(int x, int y) => false;

        public override bool AddToRenderLists(RenderLists renderLists, int x, int y, ref float layerDepth)
        {
            if (IsDisposed || !IsVisible || !World.InGame || Client.Game.Scene is not GameScene scene)
            {
                return false;
            }

            Camera camera = scene.Camera;
            Rectangle view = camera.Bounds;

            _bars.Clear();
            _seen.Clear();
            _barDepth = layerDepth;

            foreach (Mobile mobile in World.Mobiles.Values)
            {
                if (mobile.IsDestroyed)
                {
                    continue;
                }

                Point head = HeadPosition(mobile, camera);

                if (!view.Contains(head))
                {
                    continue;
                }

                _seen.Add(mobile.Serial);

                int top = head.Y;

                if (mobile.HitsMax > 0)
                {
                    top -= BAR_HEIGHT + 2;
                    int filled = Math.Clamp(BAR_WIDTH * mobile.Hits / mobile.HitsMax, 0, BAR_WIDTH);
                    _bars.Add((new Rectangle(head.X - BAR_WIDTH / 2, top, BAR_WIDTH, BAR_HEIGHT), BarColour(mobile), filled));
                }

                RenderedText name = NameText(mobile);

                if (name != null)
                {
                    renderLists.AddGumpNoAtlas(name, head.X - name.Width / 2, top - name.Height, layerDepth);
                }
            }

            if (_bars.Count > 0)
            {
                renderLists.AddGumpNoAtlas(_drawBars);
            }

            ForgetUnseen();

            return true;
        }

        public override void Dispose()
        {
            foreach (var entry in _names.Values)
            {
                entry.text.Destroy();
            }

            _names.Clear();

            base.Dispose();
        }

        /// <summary>The point just above the mobile's head, in window coordinates - the health lines' own arithmetic.</summary>
        private static Point HeadPosition(Mobile mobile, Camera camera)
        {
            Client.Game.UO.Animations.GetAnimationDimensions(
                mobile.AnimIndex,
                mobile.GetGraphicForAnimation(),
                0,
                0,
                mobile.IsMounted,
                0,
                out _,
                out int centerY,
                out _,
                out int height
            );

            Point p = mobile.RealScreenPosition;
            p.X += (int)mobile.Offset.X + 22;
            p.Y += (int)(mobile.Offset.Y - mobile.Offset.Z) + 22 - (height + centerY + 8 + 22);

            if (mobile.IsGargoyle && mobile.IsFlying)
            {
                p.Y -= 22;
            }
            else if (!mobile.IsMounted)
            {
                p.Y += 22;
            }

            return camera.WorldToScreen(p, true);
        }

        private RenderedText NameText(Mobile mobile)
        {
            if (string.IsNullOrEmpty(mobile.Name))
            {
                return null;
            }

            ushort hue = Notoriety.GetHue(mobile.NotorietyFlag);

            if (_names.TryGetValue(mobile.Serial, out var entry) && entry.name == mobile.Name && entry.hue == hue)
            {
                return entry.text;
            }

            entry.text?.Destroy();

            RenderedText text = RenderedText.Create(mobile.Name, hue, NAME_FONT, true, FontStyle.BlackBorder);
            _names[mobile.Serial] = (mobile.Name, hue, text);

            return text;
        }

        private static Color BarColour(Mobile mobile)
        {
            if (mobile.IsPoisoned)
            {
                return Color.LimeGreen;
            }

            int percent = 100 * mobile.Hits / Math.Max(1, (int)mobile.HitsMax);

            return percent > 50 ? Color.ForestGreen : percent > 25 ? Color.Gold : Color.Red;
        }

        private bool DrawBars(UltimaBatcher2D batcher)
        {
            Vector3 hue = ShaderHueTranslator.GetHueVector(0, false, 0.85f);

            foreach (var (rect, fill, filled) in _bars)
            {
                batcher.Draw(SolidColorTextureCache.GetTexture(Color.Black), new Rectangle(rect.X - 1, rect.Y - 1, rect.Width + 2, rect.Height + 2), hue, _barDepth);

                if (filled > 0)
                {
                    batcher.Draw(SolidColorTextureCache.GetTexture(fill), new Rectangle(rect.X, rect.Y, filled, rect.Height), hue, _barDepth);
                }
            }

            return true;
        }

        /// <summary>Destroys the name texts of mobiles that left the screen this frame.</summary>
        private void ForgetUnseen()
        {
            _stale.Clear();

            foreach (uint serial in _names.Keys)
            {
                if (!_seen.Contains(serial))
                {
                    _stale.Add(serial);
                }
            }

            foreach (uint serial in _stale)
            {
                _names[serial].text.Destroy();
                _names.Remove(serial);
            }
        }
    }
}
