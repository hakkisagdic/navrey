// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Threading;
using ClassicUO.Game;
using ClassicUO.Game.GameObjects;
using ClassicUO.IO;
using ClassicUO.Network;

namespace ClassicUO.Agent.Modules
{
    /// <summary>
    /// While `record` runs (and the `action_log` switch is on), writes every player action the client
    /// sends to the server as one JSON line beside the video: `x.mp4` gets `x.actions.jsonl`, a frame
    /// folder gets `actions.jsonl`. Together they are training data - what was on screen, and what
    /// the player did about it.
    ///
    /// It watches the outgoing packets rather than the game's UI code, so everything is caught
    /// whichever way it was done - a mouse click, a macro, a hotkey or an agent command - from one
    /// hook in NetClient.Send that is only installed while recording. Only player actions are
    /// written (<see cref="ActionPackets"/>); housekeeping traffic (pings, status requests) is not.
    /// A single click is both a player action and how the client asks a new mobile's name, so the
    /// server packet handlers are wrapped while recording to tell the two apart.
    ///
    /// Each line carries:
    ///   t       wall-clock time, Unix milliseconds
    ///   frame   index of the next frame the recorder keeps, so the action happened after frame
    ///           `frame - 1` was captured; with fps from the `start` line it is a video time
    ///   source  "agent" when a bridge command sent it; "client" when the client sent it on its own
    ///           while handling a server packet (asking a new mobile's name, say); "human" otherwise
    ///   action  what was done, then its own fields (serials as "0x..." strings)
    ///   state   the player as it was at that moment: position, facing, hits, stamina, mana, war
    /// plus a `start` line (video, fps) and a `stop` line (frames kept and dropped).
    ///
    /// The game thread only builds the line; a writer thread puts it on disk.
    /// </summary>
    internal static class ActionLog
    {
        private static readonly ArrayBufferWriter<byte> _buffer = new(1024);
        private static Utf8JsonWriter _json;

        private static BlockingCollection<byte[]> _queue;
        private static Thread _writer;
        private static int _actions;
        private static int _lost;

        /// <summary>The handlers in place before recording, while every server packet's handler is wrapped.</summary>
        private static readonly PacketHandlers.OnPacketBufferReader[] _originals = new PacketHandlers.OnPacketBufferReader[256];

        /// <summary>How deep the game thread is in handling server packets right now.</summary>
        private static int _handlingServerPacket;

        /// <summary>The file being written, or null when not logging.</summary>
        public static string Path { get; private set; }

        /// <summary>
        /// Starts a log beside <paramref name="target"/> (an .mp4 or a frame folder) if the
        /// `action_log` switch is on, and returns its path; null when it is off. Game thread.
        /// </summary>
        public static string Start(string target, double fps)
        {
            Stop(FrameRecorder.FramesKept, FrameRecorder.Dropped);

            if (!FeatureModules.Switches.ActionLog)
            {
                return null;
            }

            string path = target.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
                ? System.IO.Path.ChangeExtension(target, ".actions.jsonl")
                : System.IO.Path.Combine(target, "actions.jsonl");

            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path) ?? ".");

            var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            var queue = new BlockingCollection<byte[]>(4096);

            _writer = new Thread(() =>
            {
                try
                {
                    foreach (byte[] line in queue.GetConsumingEnumerable())
                    {
                        stream.Write(line, 0, line.Length);
                        stream.Flush();
                    }
                }
                catch (IOException ex)
                {
                    Output.Error($"action log: {ex.Message}");
                }
                finally
                {
                    stream.Dispose();
                }
            })
            {
                IsBackground = true,
                Name = "ActionLog.writer"
            };

            _queue = queue;
            _actions = 0;
            _lost = 0;
            Path = path;
            _writer.Start();

            Json().WriteStartObject();
            _json.WriteNumber("t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            _json.WriteString("event", "start");
            _json.WriteString("video", target);
            _json.WriteNumber("fps", fps);
            _json.WriteNumber("frame", FrameRecorder.FramesKept);
            _json.WriteEndObject();
            Enqueue();

            WrapServerHandlers();
            NetClient.OnSend += OnSend;

            return path;
        }

        /// <summary>Ends the log, if one is open, and returns how many actions it holds. Game thread.</summary>
        public static int Stop(int framesKept, int framesDropped)
        {
            if (_queue == null)
            {
                return 0;
            }

            NetClient.OnSend -= OnSend;
            UnwrapServerHandlers();

            Json().WriteStartObject();
            _json.WriteNumber("t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            _json.WriteString("event", "stop");
            _json.WriteNumber("frames", framesKept);
            _json.WriteNumber("dropped", framesDropped);
            _json.WriteNumber("actions", _actions);

            if (_lost > 0)
            {
                _json.WriteNumber("lost", _lost);
            }

            _json.WriteEndObject();
            Enqueue();

            _queue.CompleteAdding();
            _writer?.Join(TimeSpan.FromSeconds(5));

            int actions = _actions;

            _queue = null;
            _writer = null;
            Path = null;

            return actions;
        }

        /// <summary>
        /// One outgoing packet. Normally on the game thread; a packet sent from anywhere else (a
        /// plugin's own thread) is logged without the state snapshot, which only the game thread
        /// may read. Never lets an exception into NetClient.Send.
        /// </summary>
        private static void OnSend(ReadOnlySpan<byte> packet)
        {
            try
            {
                if (_queue == null || packet.IsEmpty || !ActionPackets.IsAction(packet))
                {
                    return;
                }

                bool gameThread = Thread.CurrentThread == CUOEnviroment.GameThread;

                Json().WriteStartObject();
                _json.WriteNumber("t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                _json.WriteNumber("frame", FrameRecorder.FramesKept);
                _json.WriteString("source", Source(gameThread));

                ActionPackets.Describe(packet, _json);

                World world = gameThread ? ClassicUO.Client.Game?.UO?.World : null;

                if (world?.Player != null)
                {
                    WriteState(world.Player, world.MapIndex);
                }

                _json.WriteEndObject();
                Enqueue();
                _actions++;
            }
            catch (Exception ex)
            {
                Output.Error($"action log: {ex.Message}");
            }
        }

        private static string Source(bool gameThread)
        {
            if (!gameThread)
            {
                return "other";
            }

            if (GameDispatcher.RunningAgentWork)
            {
                return "agent";
            }

            return _handlingServerPacket > 0 ? "client" : "human";
        }

        /// <summary>
        /// Wraps every installed server packet handler so a packet the client sends while handling
        /// one is known to be the client's own doing, not the player's. Undone by
        /// <see cref="UnwrapServerHandlers"/>, which restores exactly what was there.
        /// </summary>
        private static void WrapServerHandlers()
        {
            for (int id = 0; id < _originals.Length; id++)
            {
                PacketHandlers.OnPacketBufferReader original = PacketHandlers.Handler.Get((byte)id);

                if (original == null)
                {
                    continue;
                }

                _originals[id] = original;

                PacketHandlers.Handler.Add((byte)id, (World world, ref StackDataReader p) =>
                {
                    _handlingServerPacket++;

                    try
                    {
                        original(world, ref p);
                    }
                    finally
                    {
                        _handlingServerPacket--;
                    }
                });
            }
        }

        private static void UnwrapServerHandlers()
        {
            for (int id = 0; id < _originals.Length; id++)
            {
                if (_originals[id] != null)
                {
                    PacketHandlers.Handler.Add((byte)id, _originals[id]);
                    _originals[id] = null;
                }
            }

            _handlingServerPacket = 0;
        }

        private static void WriteState(PlayerMobile p, int map)
        {
            _json.WriteStartObject("state");
            _json.WriteNumber("x", p.X);
            _json.WriteNumber("y", p.Y);
            _json.WriteNumber("z", p.Z);
            _json.WriteNumber("map", map);
            _json.WriteString("dir", Directions.Name(p.Direction));
            _json.WriteNumber("hits", p.Hits);
            _json.WriteNumber("maxHits", p.HitsMax);
            _json.WriteNumber("stam", p.Stamina);
            _json.WriteNumber("maxStam", p.StaminaMax);
            _json.WriteNumber("mana", p.Mana);
            _json.WriteNumber("maxMana", p.ManaMax);
            _json.WriteBoolean("war", p.InWarMode);
            _json.WriteEndObject();
        }

        /// <summary>The line writer, reset onto an empty buffer.</summary>
        private static Utf8JsonWriter Json()
        {
            _buffer.ResetWrittenCount();

            if (_json == null)
            {
                _json = new Utf8JsonWriter(_buffer);
            }
            else
            {
                _json.Reset(_buffer);
            }

            return _json;
        }

        /// <summary>Hands the finished line to the writer thread; a full queue loses the line, never stalls the frame.</summary>
        private static void Enqueue()
        {
            _json.Flush();

            byte[] line = new byte[_buffer.WrittenCount + 1];
            _buffer.WrittenSpan.CopyTo(line);
            line[^1] = (byte)'\n';

            if (_queue == null || !_queue.TryAdd(line))
            {
                _lost++;
            }
        }
    }
}
