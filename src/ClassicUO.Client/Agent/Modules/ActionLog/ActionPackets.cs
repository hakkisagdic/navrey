// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using ClassicUO.Game.Data;
using ClassicUO.Network;

namespace ClassicUO.Agent.Modules
{
    /// <summary>
    /// Which client-to-server packets are player actions, and what each one says. A packet not
    /// listed here (pings, version and feature replies, status and name requests, chat channels)
    /// is the client talking to the server on its own account and is not an action.
    ///
    /// Layouts follow NetClient's own Send_* writers in OutgoingPackets.cs. A packet whose
    /// fields cannot be read is still logged by its id, so nothing the player did goes missing
    /// because a decoder disagreed with a shard.
    /// </summary>
    internal static class ActionPackets
    {
        public static bool IsAction(ReadOnlySpan<byte> p)
        {
            switch (p[0])
            {
                case 0x02: // walk / run
                case 0x03: // speech (ASCII)
                case 0x05: // attack
                case 0x06: // double click
                case 0x07: // pick up
                case 0x08: // drop
                case 0x09: // single click
                case 0x12: // text command: skill, spell, door, action
                case 0x13: // equip
                case 0x3A: // skill lock
                case 0x3B: // buy
                case 0x6C: // target
                case 0x6F: // secure trade
                case 0x72: // war mode
                case 0x75: // rename
                case 0x7D: // menu response
                case 0x9A: // prompt (ASCII)
                case 0x9F: // sell
                case 0xAD: // speech (unicode)
                case 0xB1: // gump response
                case 0xC2: // prompt (unicode)
                    return true;

                case 0xBF: // general information: only the subcommands a player causes
                    return p.Length >= 5 && BinaryPrimitives.ReadUInt16BigEndian(p.Slice(3)) switch
                    {
                        0x13 or 0x15 or 0x1A or 0x1C => true,
                        _ => false
                    };

                case 0xD7: // encoded commands: only special moves
                    return p.Length >= 9 && BinaryPrimitives.ReadUInt16BigEndian(p.Slice(7)) == 0x19;

                default:
                    return false;
            }
        }

        /// <summary>Writes `action`, `id` and the packet's own fields into the open JSON object.</summary>
        public static void Describe(ReadOnlySpan<byte> p, Utf8JsonWriter json)
        {
            json.WriteString("id", $"0x{p[0]:X2}");

            try
            {
                DescribeFields(p, json);
            }
            catch (IndexOutOfRangeException)
            {
                json.WriteBoolean("truncated", true);
            }
            catch (ArgumentOutOfRangeException)
            {
                json.WriteBoolean("truncated", true);
            }
        }

        private static void DescribeFields(ReadOnlySpan<byte> p, Utf8JsonWriter json)
        {
            // Variable-length packets carry a two-byte length after the id.
            int at = NetClient.Socket.PacketsTable.GetPacketLength(p[0]) < 0 ? 3 : 1;

            switch (p[0])
            {
                case 0x02:
                {
                    byte dir = p[1];
                    json.WriteString("action", (dir & 0x80) != 0 ? "run" : "walk");
                    json.WriteString("dir", Directions.Name((Direction)(dir & 0x07)));

                    break;
                }

                case 0x03:
                    json.WriteString("action", "say");
                    json.WriteString("mode", SpeechMode(p[at]));
                    json.WriteString("text", ReadAscii(p.Slice(at + 5)));

                    break;

                case 0xAD:
                    json.WriteString("action", "say");
                    json.WriteString("mode", SpeechMode(p[at]));
                    json.WriteString("text", ReadUnicodeSpeech(p.Slice(at)));

                    break;

                case 0x05:
                    json.WriteString("action", "attack");
                    WriteSerial(json, "serial", U32(p, 1));

                    break;

                case 0x06:
                {
                    uint serial = U32(p, 1);
                    json.WriteString("action", "use");
                    WriteSerial(json, "serial", serial & 0x7FFF_FFFF);

                    if ((serial & 0x8000_0000) != 0)
                    {
                        json.WriteBoolean("paperdoll", true);
                    }

                    break;
                }

                case 0x07:
                    json.WriteString("action", "pickup");
                    WriteSerial(json, "serial", U32(p, 1));
                    json.WriteNumber("amount", U16(p, 5));

                    break;

                case 0x08:
                {
                    json.WriteString("action", "drop");
                    WriteSerial(json, "serial", U32(p, 1));
                    json.WriteNumber("x", U16(p, 5));
                    json.WriteNumber("y", U16(p, 7));
                    json.WriteNumber("z", (sbyte)p[9]);

                    // 6.0.1.7 and later put a grid slot before the container.
                    uint container = U32(p, p.Length >= 15 ? 11 : 10);
                    WriteSerial(json, "container", container);

                    break;
                }

                case 0x09:
                    json.WriteString("action", "click");
                    WriteSerial(json, "serial", U32(p, 1));

                    break;

                case 0x12:
                    DescribeTextCommand(p[at], ReadAscii(p.Slice(at + 1)), json);

                    break;

                case 0x13:
                    json.WriteString("action", "equip");
                    WriteSerial(json, "serial", U32(p, 1));
                    json.WriteString("layer", ((Layer)p[5]).ToString());
                    WriteSerial(json, "container", U32(p, 6));

                    break;

                case 0x3A:
                    json.WriteString("action", "skilllock");
                    json.WriteNumber("skill", U16(p, at));
                    json.WriteString("lock", ((Lock)p[at + 2]).ToString().ToLowerInvariant());

                    break;

                case 0x3B:
                    json.WriteString("action", "buy");
                    WriteSerial(json, "vendor", U32(p, at));
                    json.WriteNumber("lines", Math.Max(0, (p.Length - at - 5) / 7));

                    break;

                case 0x9F:
                    json.WriteString("action", "sell");
                    WriteSerial(json, "vendor", U32(p, at));
                    json.WriteNumber("lines", U16(p, at + 4));

                    break;

                case 0x6C:
                    DescribeTarget(p, json);

                    break;

                case 0x6F:
                    json.WriteString("action", "trade");
                    json.WriteNumber("op", p[at]);
                    WriteSerial(json, "serial", U32(p, at + 1));

                    break;

                case 0x72:
                    json.WriteString("action", "warmode");
                    json.WriteBoolean("on", p[1] != 0);

                    break;

                case 0x75:
                    json.WriteString("action", "rename");
                    WriteSerial(json, "serial", U32(p, 1));
                    json.WriteString("name", ReadAscii(p.Slice(5, Math.Min(30, p.Length - 5))));

                    break;

                case 0x7D:
                    json.WriteString("action", "menu");
                    WriteSerial(json, "serial", U32(p, 1));
                    json.WriteNumber("menu", U16(p, 5));
                    json.WriteNumber("index", U16(p, 7));

                    break;

                case 0x9A:
                case 0xC2:
                    // The prompt's id (8 bytes), then 1 for an answer and 0 for a cancel, then the
                    // text: ASCII for 0x9A, a language code and little-endian UTF-16 for 0xC2.
                    json.WriteString("action", "prompt");
                    json.WriteBoolean("cancel", U32(p, at + 8) == 0);
                    json.WriteString("text", p[0] == 0x9A
                        ? ReadAscii(p.Slice(at + 12))
                        : Encoding.Unicode.GetString(p.Slice(Math.Min(p.Length, at + 16))).TrimEnd('\0'));

                    break;

                case 0xB1:
                {
                    json.WriteString("action", "gumpresponse");
                    WriteSerial(json, "serial", U32(p, at));
                    WriteSerial(json, "gump", U32(p, at + 4));
                    json.WriteNumber("button", U32(p, at + 8));

                    int switches = (int)Math.Min(U32(p, at + 12), 64);
                    json.WriteStartArray("switches");

                    for (int i = 0; i < switches; i++)
                    {
                        json.WriteNumberValue(U32(p, at + 16 + i * 4));
                    }

                    json.WriteEndArray();

                    break;
                }

                case 0xBF:
                    DescribeGeneral(p, at, json);

                    break;

                case 0xD7: // player serial, 0x19, four zero bytes, the ability's index
                    json.WriteString("action", "ability");
                    json.WriteNumber("ability", p[at + 10]);

                    break;
            }
        }

        private static void DescribeTextCommand(byte type, string text, Utf8JsonWriter json)
        {
            switch (type)
            {
                case 0x24: // "<skill id> 0"
                    json.WriteString("action", "useskill");
                    json.WriteString("skill", text.Split(' ')[0]);

                    break;

                case 0x27:
                case 0x56:
                    json.WriteString("action", "cast");
                    json.WriteString("spell", text);

                    break;

                case 0x43:
                    json.WriteString("action", "openspellbook");

                    break;

                case 0x58:
                    json.WriteString("action", "opendoor");

                    break;

                case 0xC7:
                    json.WriteString("action", "emote");
                    json.WriteString("text", text);

                    break;

                default:
                    json.WriteString("action", "command");
                    json.WriteNumber("type", type);
                    json.WriteString("text", text);

                    break;
            }
        }

        private static void DescribeTarget(ReadOnlySpan<byte> p, Utf8JsonWriter json)
        {
            // type, cursor id, cursor flags, serial, x, y, z (two bytes), graphic. The client
            // cancels a cursor by answering with no serial and x = y = 0xFFFF.
            byte type = p[1];
            uint serial = U32(p, 7);
            ushort x = U16(p, 11);
            ushort y = U16(p, 13);

            json.WriteString("action", "target");

            if (p[6] == 3 || (serial == 0 && x == 0xFFFF && y == 0xFFFF))
            {
                json.WriteBoolean("cancel", true);

                return;
            }

            if (type == 0)
            {
                WriteSerial(json, "serial", serial);
            }
            else
            {
                json.WriteNumber("x", x);
                json.WriteNumber("y", y);
                json.WriteNumber("z", (short)U16(p, 15));
                json.WriteNumber("graphic", U16(p, 17));
            }
        }

        private static void DescribeGeneral(ReadOnlySpan<byte> p, int at, Utf8JsonWriter json)
        {
            ushort sub = U16(p, at);
            int body = at + 2;

            switch (sub)
            {
                case 0x13:
                    json.WriteString("action", "contextmenu");
                    WriteSerial(json, "serial", U32(p, body));

                    break;

                case 0x15:
                    json.WriteString("action", "contextpick");
                    WriteSerial(json, "serial", U32(p, body));
                    json.WriteNumber("index", U16(p, body + 4));

                    break;

                case 0x1A:
                    json.WriteString("action", "statlock");
                    json.WriteString("stat", p[body] switch { 0 => "str", 1 => "dex", _ => "int" });
                    json.WriteString("lock", ((Lock)p[body + 1]).ToString().ToLowerInvariant());

                    break;

                case 0x1C: // a spell cast from a book or icon: 0x02, then the spell id
                    json.WriteString("action", "cast");
                    json.WriteString("spell", U16(p, body + 2).ToString());

                    break;
            }
        }

        private static string SpeechMode(byte type) => ((MessageType)(type & ~0xC0)).ToString().ToLowerInvariant();

        /// <summary>
        /// The text of a 0xAD speech request, from its type byte on. With the keyword flag set,
        /// a 12-bit keyword count and 12-bit keyword ids come first and the text is UTF-8;
        /// otherwise it is big-endian UTF-16. Both end at a terminator.
        /// </summary>
        private static string ReadUnicodeSpeech(ReadOnlySpan<byte> p)
        {
            byte type = p[0];
            ReadOnlySpan<byte> text = p.Slice(9);

            if ((type & 0xC0) == 0xC0)
            {
                int keywords = (text[0] << 4) | (text[1] >> 4);
                int skip = (12 + 12 * keywords + 7) / 8;
                text = text.Slice(Math.Min(skip, text.Length));

                int end = text.IndexOf((byte)0);

                return Encoding.UTF8.GetString(end < 0 ? text : text.Slice(0, end));
            }

            var chars = new StringBuilder(text.Length / 2);

            for (int i = 0; i + 1 < text.Length; i += 2)
            {
                char c = (char)((text[i] << 8) | text[i + 1]);

                if (c == '\0')
                {
                    break;
                }

                chars.Append(c);
            }

            return chars.ToString();
        }

        private static string ReadAscii(ReadOnlySpan<byte> bytes)
        {
            int end = bytes.IndexOf((byte)0);

            return Encoding.ASCII.GetString(end < 0 ? bytes : bytes.Slice(0, end));
        }

        private static void WriteSerial(Utf8JsonWriter json, string name, uint serial) =>
            json.WriteString(name, $"0x{serial:X8}");

        private static uint U32(ReadOnlySpan<byte> p, int at) => BinaryPrimitives.ReadUInt32BigEndian(p.Slice(at, 4));

        private static ushort U16(ReadOnlySpan<byte> p, int at) => BinaryPrimitives.ReadUInt16BigEndian(p.Slice(at, 2));
    }
}
