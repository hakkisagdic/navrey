// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using ClassicUO.Game;
using ClassicUO.Game.GameObjects;

namespace ClassicUO.Agent.Modules
{
    /// <summary>
    /// `travel` and long `goto` when the `world_pathfinder` feature is on: plan the whole route with
    /// <see cref="WorldRoute"/>, then walk it a leg at a time with Navrey's own exact planner -
    /// each leg aimed at the route point <see cref="LEG_TILES"/> ahead of where the character
    /// actually is. A leg that gets nowhere bars the next stretch of the route - the map files
    /// missed whatever blocks it - and the route is planned again from there; after
    /// <see cref="MAX_REPLANS"/> of those, or when no route exists, it hands back to the
    /// straight-line hops the feature replaces.
    /// </summary>
    internal static class RoutedTravel
    {
        /// <summary>
        /// Route tiles per leg. Short enough that the exact planner's leg stays close to the
        /// coarse route, long enough that its search per leg stays small and fast.
        /// </summary>
        private const int LEG_TILES = 60;

        /// <summary>New routes planned after legs that made no progress before giving up on routing.</summary>
        private const int MAX_REPLANS = 3;

        /// <summary>Route tiles barred ahead of the character after a leg that made no progress.</summary>
        private const int STUCK_TILES = 4;

        /// <summary>
        /// Walks to (<paramref name="tx"/>, <paramref name="ty"/>) along a planned route. Null means
        /// no route could be planned (or routing gave up) and the caller should go on the old way
        /// from wherever the character is; otherwise the outcome, as Navigation.Travel reports it.
        /// </summary>
        public static bool? Travel(CommandContext ctx, int tx, int ty, HashSet<(int x, int y)> avoid, int? tz)
        {
            var barred = avoid == null ? new HashSet<(int x, int y)>() : new HashSet<(int x, int y)>(avoid);
            int replans = 0;

            var start = Position(ctx);

            if (Math.Max(Math.Abs(tx - start.x), Math.Abs(ty - start.y)) <= 1)
            {
                return null; // already there: the hop loop reports the arrival as it always has
            }

            while (!ctx.Cancel.IsCancellationRequested)
            {
                List<(int x, int y)> route = PlanFromHere(ctx, tx, ty, barred);

                if (route == null)
                {
                    ctx.ClearFailure();

                    return null;
                }

                bool? walked = WalkRoute(ctx, route, tx, ty, avoid, tz, barred);

                if (walked != null)
                {
                    return walked.Value;
                }

                if (++replans > MAX_REPLANS)
                {
                    var here = Position(ctx);
                    ctx.Info($"Travel: route stuck {MAX_REPLANS} times - continuing in straight-line hops from ({here.x}, {here.y})");

                    // The hops report their own outcome; a stuck leg's failure is not it.
                    ctx.ClearFailure();

                    return null;
                }
            }

            return false;
        }

        /// <summary>A route from the character's tile, or null (with the reason printed) when there is none.</summary>
        private static List<(int x, int y)> PlanFromHere(CommandContext ctx, int tx, int ty, HashSet<(int x, int y)> barred)
        {
            var from = Position(ctx);
            List<WorldRoute.HousePiece> houses = ctx.Game(w => Houses(w), "WorldRoute.Houses");

            WorldRoute.Result plan = WorldRoute.Plan(from.map, from.x, from.y, tx, ty, houses, barred, ctx.Cancel);

            if (plan.Path.Count == 0)
            {
                ctx.Info($"Travel: no route over the map from ({from.x}, {from.y}) to ({tx}, {ty}) - {plan.Ended} " +
                         $"after {plan.Nodes} nodes in {plan.ElapsedMs}ms");

                return null;
            }

            var (endX, endY) = plan.Path[^1];
            string reach = plan.Partial
                ? $"to ({endX}, {endY}), the closest the map files allow - the last stretch is the exact planner's"
                : $"to ({plan.GoalX}, {plan.GoalY})";

            ctx.Info($"Travel: route of {plan.Path.Count} tiles {reach}, planned over the map in {plan.ElapsedMs}ms ({plan.Nodes} nodes)");

            return plan.Path;
        }

        /// <summary>
        /// Walks <paramref name="route"/> leg by leg. True or false is the journey's outcome; null
        /// means a leg made no progress, its waypoint is now barred, and a new route is wanted.
        /// </summary>
        private static bool? WalkRoute(CommandContext ctx, List<(int x, int y)> route, int tx, int ty,
                                       HashSet<(int x, int y)> avoid, int? tz, HashSet<(int x, int y)> barred)
        {
            int along = 0;

            while (!ctx.Cancel.IsCancellationRequested)
            {
                var here = Position(ctx);

                if (Math.Max(Math.Abs(tx - here.x), Math.Abs(ty - here.y)) <= 1 && Capabilities.GridPathfinder.OnGoalFloor(here.z, tz))
                {
                    ctx.Print($"Arrived at ({here.x}, {here.y})");

                    return true;
                }

                along = Nearest(route, here.x, here.y, along);
                int next = Math.Min(along + LEG_TILES, route.Count - 1);
                bool finalLeg = next == route.Count - 1;

                int wx = finalLeg ? tx : route[next].x;
                int wy = finalLeg ? ty : route[next].y;

                ctx.Info($"Travel: leg to ({wx}, {wy}) - {route.Count - 1 - along} route tiles left to ({tx}, {ty})");

                bool legOk = Capabilities.Navigation.GoToNear(ctx, wx, wy, avoid: avoid, tz: finalLeg ? tz : null);

                if (ctx.Cancel.IsCancellationRequested)
                {
                    return false;
                }

                var after = Position(ctx);

                if (finalLeg)
                {
                    // The leg settles for the closest reachable tile, so false is "not one step".
                    if (!legOk)
                    {
                        return false;
                    }

                    ctx.ClearFailure();
                    ctx.Print($"Travel finished at ({after.x}, {after.y}) - target ({tx}, {ty})");

                    return true;
                }

                if (after.x == here.x && after.y == here.y)
                {
                    for (int i = along + 1; i <= Math.Min(along + STUCK_TILES, route.Count - 2); i++)
                    {
                        barred.Add(route[i]);
                    }

                    ctx.Info($"Travel: no progress toward ({wx}, {wy}) - planning around the next stretch of the route");

                    return null;
                }

                ctx.ClearFailure();
            }

            return false;
        }

        /// <summary>
        /// The route point nearest the character, looking from <paramref name="from"/> onward so a
        /// route that doubles back on itself is not cut short.
        /// </summary>
        private static int Nearest(List<(int x, int y)> route, int x, int y, int from)
        {
            int best = from;
            int bestDistance = int.MaxValue;
            int limit = Math.Min(route.Count, from + LEG_TILES * 3);

            for (int i = from; i < limit; i++)
            {
                int d = Math.Max(Math.Abs(route[i].x - x), Math.Abs(route[i].y - y));

                if (d < bestDistance)
                {
                    bestDistance = d;
                    best = i;
                }
            }

            return best;
        }

        /// <summary>Every house piece the client knows, copied for the planner. Game thread.</summary>
        private static List<WorldRoute.HousePiece> Houses(World world)
        {
            var pieces = new List<WorldRoute.HousePiece>();

            foreach (House house in world.HouseManager.Houses)
            {
                foreach (Multi multi in house.Components)
                {
                    if (!multi.IsDestroyed)
                    {
                        pieces.Add(new WorldRoute.HousePiece(multi.X, multi.Y, multi.Z, multi.Graphic, (int)multi.State));
                    }
                }
            }

            return pieces;
        }

        private static (int x, int y, sbyte z, int map) Position(CommandContext ctx) =>
            ctx.Game(w => ((int)w.Player.X, (int)w.Player.Y, w.Player.Z, w.MapIndex));
    }
}
