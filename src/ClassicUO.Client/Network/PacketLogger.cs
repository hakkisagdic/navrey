using ClassicUO.Utility;
using ClassicUO.Utility.Logging;
using System;

namespace ClassicUO.Network
{
    sealed class PacketLogger
    {
        public static PacketLogger Default { get; set; } = new PacketLogger();



        private LogFile _logFile;

        /// <summary>Packet ids to log when <see cref="_filtered"/>; every id otherwise.</summary>
        private readonly bool[] _only = new bool[256];
        private bool _filtered;

        public bool Enabled { get; set; }

        /// <summary>
        /// Restricts the log to the comma-separated packet ids in <paramref name="ids"/>, written
        /// as hex with or without 0x ("0x02,AD,1c"). Account packets stay hidden either way.
        /// </summary>
        public void LogOnly(string ids)
        {
            foreach (string part in ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string hex = part.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? part.Substring(2) : part;

                if (byte.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out byte id))
                {
                    _only[id] = true;
                    _filtered = true;
                }
                else
                {
                    Utility.Logging.Log.Warn($"-packetlog: '{part}' is not a packet id");
                }
            }
        }



        public LogFile CreateFile()
        {
            _logFile?.Dispose();
            return _logFile = new LogFile(FileSystemHelper.CreateFolderIfNotExists(CUOEnviroment.ExecutablePath, "Logs", "Network"), "packets.log");
        }

        public void Log(Span<byte> message, bool toServer)
        {
            if (!Enabled || (_filtered && !_only[message[0]])) return;

            Span<char> span = stackalloc char[256];
            var output = new ValueStringBuilder(span);
            {
                int off = sizeof(ulong) + 2;

                output.Append(' ', off);
                output.Append(string.Format("Ticks: {0} | {1} |  ID: {2:X2}   Length: {3}\n", Time.Ticks, (toServer ? "Client -> Server" : "Server -> Client"), message[0], message.Length));

                if (message[0] == 0x80 || message[0] == 0x91)
                {
                    output.Append(' ', off);
                    output.Append("[ACCOUNT CREDENTIALS HIDDEN]\n");
                }
                else
                {
                    output.Append(' ', off);
                    output.Append("0  1  2  3  4  5  6  7   8  9  A  B  C  D  E  F\n");

                    output.Append(' ', off);
                    output.Append("-- -- -- -- -- -- -- --  -- -- -- -- -- -- -- --\n");

                    ulong address = 0;

                    for (int i = 0; i < message.Length; i += 16, address += 16)
                    {
                        output.Append($"{address:X8}");

                        for (int j = 0; j < 16; ++j)
                        {
                            if ((j % 8) == 0)
                            {
                                output.Append(" ");
                            }

                            if (i + j < message.Length)
                            {
                                output.Append($" {message[i + j]:X2}");
                            }
                            else
                            {
                                output.Append("   ");
                            }
                        }

                        output.Append("  ");

                        for (int j = 0; j < 16 && i + j < message.Length; ++j)
                        {
                            var c = message[i + j];

                            if (c >= 0x20 && c < 0x80)
                            {
                                output.Append((char)c);
                            }
                            else
                            {
                                output.Append('.');
                            }
                        }

                        output.Append('\n');
                    }
                }

                output.Append('\n');
                output.Append('\n');

                var s = output.ToString();

                if (_logFile != null)
                {
                    _logFile.Write(s);
                }
                else
                {
                    Console.WriteLine(s);
                }

                output.Dispose();
            }
        }
    }
}
