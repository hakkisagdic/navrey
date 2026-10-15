// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using ClassicUO.Assets;
using ClassicUO.IO;

namespace ClassicUO.Agent.Modules
{
    /// <summary>
    /// Plans a whole long route over the map files before a step is taken - the
    /// `world_pathfinder` feature, after TazUO's WorldMapPathfinder.
    ///
    /// Navrey's own planner is exact (it sees heights, doors and items) but plans one hop at a
    /// time along a straight line, so a lake or a mountain range on that line costs a detour or a
    /// dead end. This one is coarse - land and statics only, one floor, no items - but it sees the
    /// whole map at once: A* over walkability read straight from the map and statics files, on
    /// the calling (walk) thread, through its own file handles, so the game thread loads nothing
    /// and the client's own readers are never shared. <see cref="RoutedTravel"/> then walks the
    /// result leg by leg with the exact planner.
    ///
    /// The search runs over dense arrays covering the start, the goal and <see cref="MARGIN"/>
    /// tiles around them rather than over dictionaries and node objects: the client runs as a
    /// Debug build here, and the hashed version spent five seconds on 150,000 nodes.
    /// </summary>
    internal static class WorldRoute
    {
        /// <summary>Hard cap on expanded nodes.</summary>
        private const int MAX_NODES = 1_000_000;

        /// <summary>Wall-clock cap on one search.</summary>
        private const int MAX_SEARCH_MS = 5000;

        /// <summary>How far the search may stray outside the box spanned by start and goal.</summary>
        private const int MARGIN = 160;

        /// <summary>Longest side of the searched area - wider than any facet, so it only bounds the arrays.</summary>
        private const int MAX_SIDE = 7200;

        /// <summary>How far a goal that cannot be stood on is moved to the nearest tile that can.</summary>
        private const int SNAP_RADIUS = 8;

        /// <summary>
        /// A search that cannot reach the goal still returns the way to the closest tile it found,
        /// when that is this near: the goal is then usually inside a building or up a floor, which
        /// the exact planner reaches from there.
        /// </summary>
        private const int PARTIAL_REACH = 48;

        // Weighted A*: 5/4 of the octile distance halves the search and still backs out of dead ends.
        private const int HEURISTIC_WEIGHT_NUM = 5;
        private const int HEURISTIC_WEIGHT_DEN = 4;

        /// <summary>Statics up to this far above the land count as ground level; higher ones are arches and upper floors.</summary>
        private const int GROUND_LEVEL_THRESHOLD = 6;

        /// <summary>House pieces up to this far above the land are the ground floor, which blocks.</summary>
        private const int HOUSE_GROUND_BAND = 14;

        private const int CHMOF_IGNORE_IN_RENDER = 0x40;

        private const byte UNKNOWN = 0, OPEN = 1, BLOCKED = 2;

        // Direction order of ClassicUO.Game.Data.Direction: N, NE, E, SE, S, SW, W, NW.
        private static readonly int[] DX = { 0, 1, 1, 1, 0, -1, -1, -1 };
        private static readonly int[] DY = { -1, -1, 0, 1, 1, 1, 0, -1 };

        /// <summary>One piece of a house, copied off the game thread's objects before planning.</summary>
        public readonly record struct HousePiece(int X, int Y, sbyte Z, ushort Graphic, int State);

        /// <summary>What a search found.</summary>
        public sealed class Result
        {
            /// <summary>The tiles from the start (excluded) to the goal, or empty when there is no route.</summary>
            public List<(int x, int y)> Path { get; } = new();

            public int GoalX { get; set; }
            public int GoalY { get; set; }
            public int Nodes { get; set; }
            public long ElapsedMs { get; set; }

            /// <summary>goal, nogoal (target and its surroundings unwalkable), exhausted, nodes, timeout, cancelled.</summary>
            public string Ended { get; set; }

            /// <summary>True when <see cref="Path"/> stops short of the goal, at the closest tile the search reached.</summary>
            public bool Partial { get; set; }
        }

        /// <summary>
        /// Plans from (<paramref name="startX"/>, <paramref name="startY"/>) to
        /// (<paramref name="destX"/>, <paramref name="destY"/>) on <paramref name="map"/>, never
        /// through a tile in <paramref name="blocked"/> or under a house's ground floor.
        /// </summary>
        public static Result Plan(int map, int startX, int startY, int destX, int destY,
                                  IReadOnlyList<HousePiece> houses, IReadOnlyCollection<(int x, int y)> blocked,
                                  CancellationToken cancel)
        {
            var clock = Stopwatch.StartNew();
            var result = new Result { GoalX = destX, GoalY = destY };

            using var grid = new Grid(map, startX, startY, destX, destY, blocked);

            grid.AddHouses(houses);

            if (!grid.Walkable(destX, destY))
            {
                if (!Snap(grid, destX, destY, out int gx, out int gy))
                {
                    result.Ended = "nogoal";
                    result.ElapsedMs = clock.ElapsedMilliseconds;

                    return result;
                }

                result.GoalX = gx;
                result.GoalY = gy;
            }

            Search(grid, startX, startY, result, cancel, clock);
            result.ElapsedMs = clock.ElapsedMilliseconds;

            return result;
        }

        private static void Search(Grid grid, int sx, int sy, Result result, CancellationToken cancel, Stopwatch clock)
        {
            int gx = result.GoalX, gy = result.GoalY;
            int start = grid.Index(sx, sy);
            int goal = grid.Index(gx, gy);

            int[] cost = grid.Cost;
            byte[] came = grid.Came;
            var open = new PriorityQueue<int, int>();

            cost[start] = 0;
            open.Enqueue(start, Heuristic(sx, sy, gx, gy));

            int expanded = 0;
            int closest = start;
            int closestDistance = Chebyshev(sx, sy, gx, gy);
            result.Ended = "exhausted";

            while (open.TryDequeue(out int current, out int priority))
            {
                int cx = grid.X(current), cy = grid.Y(current);

                // No decrease-key in PriorityQueue, so stale duplicates are skipped here.
                if (priority - Heuristic(cx, cy, gx, gy) > cost[current])
                {
                    continue;
                }

                if (current == goal)
                {
                    result.Ended = "goal";

                    break;
                }

                int distance = Chebyshev(cx, cy, gx, gy);

                if (distance < closestDistance)
                {
                    closestDistance = distance;
                    closest = current;
                }

                if (++expanded >= MAX_NODES)
                {
                    result.Ended = "nodes";

                    break;
                }

                if ((expanded & 0xFFF) == 0)
                {
                    if (cancel.IsCancellationRequested)
                    {
                        result.Ended = "cancelled";

                        break;
                    }

                    if (clock.ElapsedMilliseconds >= MAX_SEARCH_MS)
                    {
                        result.Ended = "timeout";

                        break;
                    }
                }

                for (int d = 0; d < 8; d++)
                {
                    int nx = cx + DX[d];
                    int ny = cy + DY[d];

                    if (!grid.Walkable(nx, ny))
                    {
                        continue;
                    }

                    // No cutting a corner: both flanking tiles of a diagonal must be walkable too.
                    if ((d & 1) == 1 && (!grid.Walkable(nx, cy) || !grid.Walkable(cx, ny)))
                    {
                        continue;
                    }

                    int next = grid.Index(nx, ny);
                    int g = cost[current] + ((d & 1) == 0 ? 10 : 14);

                    if (g >= cost[next])
                    {
                        continue;
                    }

                    cost[next] = g;
                    came[next] = (byte)(d + 1);
                    open.Enqueue(next, g + Heuristic(nx, ny, gx, gy));
                }
            }

            result.Nodes = expanded;

            int end = goal;

            if (result.Ended != "goal")
            {
                if (closest == start || closestDistance > PARTIAL_REACH)
                {
                    return;
                }

                end = closest;
                result.Partial = true;
            }

            for (int at = end; at != start;)
            {
                int d = came[at] - 1;
                int x = grid.X(at), y = grid.Y(at);

                result.Path.Add((x, y));
                at = grid.Index(x - DX[d], y - DY[d]);
            }

            result.Path.Reverse();
        }

        private static int Chebyshev(int x1, int y1, int x2, int y2) => Math.Max(Math.Abs(x1 - x2), Math.Abs(y1 - y2));

        /// <summary>Octile distance (10 straight, 14 diagonal), weighted.</summary>
        private static int Heuristic(int x1, int y1, int x2, int y2)
        {
            int dx = Math.Abs(x1 - x2);
            int dy = Math.Abs(y1 - y2);

            return (10 * Math.Max(dx, dy) + 4 * Math.Min(dx, dy)) * HEURISTIC_WEIGHT_NUM / HEURISTIC_WEIGHT_DEN;
        }

        /// <summary>The nearest walkable tile to (cx, cy), searching outward ring by ring.</summary>
        private static bool Snap(Grid grid, int cx, int cy, out int x, out int y)
        {
            for (int r = 1; r <= SNAP_RADIUS; r++)
            {
                for (int dy = -r; dy <= r; dy++)
                {
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) == r && grid.Walkable(cx + dx, cy + dy))
                        {
                            x = cx + dx;
                            y = cy + dy;

                            return true;
                        }
                    }
                }
            }

            x = cx;
            y = cy;

            return false;
        }

        /// <summary>
        /// The searched area: walkability (filled a map chunk at a time, on first touch), the best
        /// cost found per tile and the direction it was reached from. Reads the map and statics
        /// files through handles of its own - the client's readers keep a seek position and belong
        /// to the game thread - and closes them with the search.
        /// </summary>
        private sealed class Grid : IDisposable
        {
            private readonly int _map;
            private readonly int _minX, _minY, _width, _height;
            private readonly byte[] _walk;
            private readonly HashSet<long> _barred = new();
            private readonly MapLoader _maps = Client.Game.UO.FileManager.Maps;
            private readonly TileDataLoader _tileData = Client.Game.UO.FileManager.TileData;
            private readonly Dictionary<string, UOFile> _files = new();
            private StaticsBlock[] _statics = new StaticsBlock[64];

            public int[] Cost { get; }
            public byte[] Came { get; }

            public Grid(int map, int sx, int sy, int gx, int gy, IReadOnlyCollection<(int x, int y)> blocked)
            {
                _map = map;

                int mapWidth = _maps.MapBlocksSize[map, 0] << 3;
                int mapHeight = _maps.MapBlocksSize[map, 1] << 3;

                _minX = Math.Max(0, Math.Min(sx, gx) - MARGIN);
                _minY = Math.Max(0, Math.Min(sy, gy) - MARGIN);
                _width = Math.Min(Math.Min(mapWidth, Math.Max(sx, gx) + MARGIN + 1) - _minX, MAX_SIDE);
                _height = Math.Min(Math.Min(mapHeight, Math.Max(sy, gy) + MARGIN + 1) - _minY, MAX_SIDE);

                int cells = _width * _height;
                _walk = new byte[cells];
                Came = new byte[cells];
                Cost = new int[cells];
                Array.Fill(Cost, int.MaxValue);

                foreach (var (x, y) in blocked)
                {
                    _barred.Add(Key(x, y));
                }
            }

            public int Index(int x, int y) => (y - _minY) * _width + (x - _minX);

            public int X(int index) => index % _width + _minX;

            public int Y(int index) => index / _width + _minY;

            public bool Walkable(int x, int y)
            {
                if (x < _minX || y < _minY || x >= _minX + _width || y >= _minY + _height)
                {
                    return false;
                }

                int i = Index(x, y);

                if (_walk[i] == UNKNOWN)
                {
                    Load(x >> 3, y >> 3);
                }

                return _walk[i] == OPEN;
            }

            /// <summary>Bars the ground floor of every house, which the map files know nothing about.</summary>
            public void AddHouses(IReadOnlyList<HousePiece> houses)
            {
                if (houses == null)
                {
                    return;
                }

                StaticTiles[] statics = _tileData.StaticData;

                foreach (HousePiece piece in houses)
                {
                    if ((piece.State & CHMOF_IGNORE_IN_RENDER) != 0 || piece.Graphic >= statics.Length || statics[piece.Graphic].IsRoof)
                    {
                        continue;
                    }

                    if (!Walkable(piece.X, piece.Y))
                    {
                        continue;
                    }

                    if (piece.Z <= LandZ(piece.X, piece.Y) + HOUSE_GROUND_BAND)
                    {
                        _walk[Index(piece.X, piece.Y)] = BLOCKED;
                    }
                }
            }

            private sbyte LandZ(int x, int y)
            {
                IndexMap im = _maps.GetIndex(_map, x >> 3, y >> 3);

                if (!im.IsValid() || im.MapFile == null)
                {
                    return 0;
                }

                UOFile file = Open(im.MapFile.FilePath);
                file.Seek((long)im.MapAddress, SeekOrigin.Begin);

                return file.Read<MapBlock>().Cells[((y & 7) << 3) + (x & 7)].Z;
            }

            /// <summary>
            /// Fills one map chunk's walkability: land that is not impassable, unless a wall, tree or
            /// rock stands on it at ground level; a floor or bridge makes a tile walkable even over
            /// water. Tiles outside the area, and tiles the caller barred, stay blocked.
            /// </summary>
            private void Load(int cx, int cy)
            {
                Span<bool> walkable = stackalloc bool[64];
                Span<sbyte> landZ = stackalloc sbyte[64];

                if (cx < _maps.MapBlocksSize[_map, 0] && cy < _maps.MapBlocksSize[_map, 1])
                {
                    ReadChunk(cx, cy, walkable, landZ);
                }

                for (int i = 0; i < 64; i++)
                {
                    int x = (cx << 3) + (i & 7);
                    int y = (cy << 3) + (i >> 3);

                    if (x < _minX || y < _minY || x >= _minX + _width || y >= _minY + _height)
                    {
                        continue;
                    }

                    _walk[Index(x, y)] = walkable[i] && !_barred.Contains(Key(x, y)) ? OPEN : BLOCKED;
                }
            }

            private void ReadChunk(int cx, int cy, Span<bool> walkable, Span<sbyte> landZ)
            {
                IndexMap im = _maps.GetIndex(_map, cx, cy);

                if (!im.IsValid() || im.MapFile == null)
                {
                    return;
                }

                UOFile mapFile = Open(im.MapFile.FilePath);
                mapFile.Seek((long)im.MapAddress, SeekOrigin.Begin);
                MapCellsArray cells = mapFile.Read<MapBlock>().Cells;

                for (int i = 0; i < 64; i++)
                {
                    ushort land = (ushort)(cells[i].TileID & 0x3FFF);

                    landZ[i] = cells[i].Z;
                    walkable[i] = land < _tileData.LandData.Length && !_tileData.LandData[land].IsImpassable;
                }

                if (im.StaticCount == 0 || im.StaticFile == null)
                {
                    return;
                }

                int count = (int)im.StaticCount;

                if (_statics.Length < count)
                {
                    _statics = new StaticsBlock[count];
                }

                Span<StaticsBlock> statics = _statics.AsSpan(0, count);
                UOFile staticFile = Open(im.StaticFile.FilePath);
                staticFile.Seek((long)im.StaticAddress, SeekOrigin.Begin);
                staticFile.Read(MemoryMarshal.AsBytes(statics));

                Span<bool> hardBlocked = stackalloc bool[64];
                Span<bool> surface = stackalloc bool[64];

                foreach (ref StaticsBlock s in statics)
                {
                    int i = ((s.Y & 7) << 3) + (s.X & 7);

                    if (s.Color >= _tileData.StaticData.Length)
                    {
                        continue;
                    }

                    ref StaticTiles data = ref _tileData.StaticData[s.Color];

                    if (data.IsRoof)
                    {
                        continue;
                    }

                    if (data.IsImpassable || data.IsWall)
                    {
                        if (s.Z <= landZ[i] + GROUND_LEVEL_THRESHOLD)
                        {
                            hardBlocked[i] = true;
                        }
                    }
                    else if (data.IsSurface || data.IsBridge)
                    {
                        surface[i] = true;
                    }
                }

                for (int i = 0; i < 64; i++)
                {
                    if (hardBlocked[i])
                    {
                        walkable[i] = false;
                    }
                    else if (surface[i])
                    {
                        walkable[i] = true;
                    }
                }
            }

            private UOFile Open(string path)
            {
                if (!_files.TryGetValue(path, out UOFile file))
                {
                    file = new UOFile(path);
                    _files[path] = file;
                }

                return file;
            }

            public void Dispose()
            {
                foreach (UOFile file in _files.Values)
                {
                    file.Dispose();
                }

                _files.Clear();
            }

            private static long Key(int x, int y) => ((long)(uint)x << 32) | (uint)y;
        }
    }
}
