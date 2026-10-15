// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ClassicUO.Agent
{
    /// <summary>
    /// Records the game's own frames, a few a second, for a video of a session. It reads the back
    /// buffer the way the PrintScreen screenshot does, so it records the game and nothing else on the
    /// screen, and needs no screen-recording permission. GameController.Draw calls
    /// <see cref="Capture"/> once a frame; with the window hidden there are no frames to record.
    ///
    /// Two outputs: a folder of numbered JPEGs (frame_000000.jpg ...), or, for a path ending in .mp4,
    /// raw frames piped into ffmpeg, which encodes the video as it goes. FNA writes JPEGs at full
    /// quality, about 1.5 MB a frame at 1150x1062, so a half-hour session would leave gigabytes of
    /// frames; the pipe leaves only the video. The game thread only copies the frame: a writer thread
    /// feeds ffmpeg, and a frame is dropped rather than waited for when ffmpeg falls behind.
    /// </summary>
    internal static class FrameRecorder
    {
        private static readonly Stopwatch _clock = Stopwatch.StartNew();
        private static string _dir;
        private static long _everyMs;
        private static long _next;
        private static int _count;
        private static int _dropped;
        private static Color[] _pixels;

        private static Process _ffmpeg;
        private static BlockingCollection<byte[]> _queue;
        private static Thread _writer;
        private static string _video;
        private static double _fps;
        private static int _width, _height;

        public static bool Recording => _dir != null || _video != null;

        /// <summary>Begins recording into a folder, or into an .mp4 file through ffmpeg. Game thread.</summary>
        public static void Start(string target, double fps)
        {
            Stop();
            _everyMs = (long)(1000 / fps);
            _next = 0;
            _count = 0;
            _dropped = 0;

            if (target.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target) ?? ".");
                _video = target;
                _fps = fps;
                _width = _height = 0; // ffmpeg starts with the first frame, once its size is known
            }
            else
            {
                Directory.CreateDirectory(target);
                _dir = target;
            }
        }

        /// <summary>Ends recording and, for a video, waits for ffmpeg to finish it; the number of frames kept. Game thread.</summary>
        public static int Stop()
        {
            int frames = _count;
            _dir = null;

            if (_video != null)
            {
                _video = null;
                _queue?.CompleteAdding();
                _writer?.Join(TimeSpan.FromSeconds(30));

                try
                {
                    _ffmpeg?.StandardInput.Close();
                    _ffmpeg?.WaitForExit(60_000);
                }
                catch (Exception)
                {
                    // ffmpeg already gone: what it wrote stays
                }

                _ffmpeg?.Dispose();
                _ffmpeg = null;
                _queue = null;
                _writer = null;
            }

            return frames;
        }

        /// <summary>Frames dropped because ffmpeg could not keep up.</summary>
        public static int Dropped => _dropped;

        /// <summary>Frames kept so far: the index the next kept frame will have in the video or folder.</summary>
        public static int FramesKept => _count;

        public static void Capture(GraphicsDevice device, int width, int height)
        {
            if (!Recording || _clock.ElapsedMilliseconds < _next || width <= 0 || height <= 0)
            {
                return;
            }

            _next = _clock.ElapsedMilliseconds + _everyMs;

            if (_pixels == null || _pixels.Length != width * height)
            {
                _pixels = new Color[width * height];
            }

            device.GetBackBufferData(_pixels);

            if (_video != null)
            {
                if (_ffmpeg == null)
                {
                    StartFfmpeg(width, height);
                }

                if (width != _width || height != _height)
                {
                    _dropped++; // the window was resized: ffmpeg was told the first size

                    return;
                }

                byte[] frame = MemoryMarshal.AsBytes(_pixels.AsSpan()).ToArray();

                if (_queue.TryAdd(frame))
                {
                    _count++;
                }
                else
                {
                    _dropped++;
                }

                return;
            }

            using Texture2D texture = new Texture2D(device, width, height, false, SurfaceFormat.Color);
            texture.SetData(_pixels);

            using FileStream file = File.Create(Path.Combine(_dir, $"frame_{_count:D6}.jpg"));
            texture.SaveAsJpeg(file, width, height);
            _count++;
        }

        private static void StartFfmpeg(int width, int height)
        {
            _width = width;
            _height = height;
            string fps = _fps.ToString(System.Globalization.CultureInfo.InvariantCulture);
            // Even dimensions for yuv420p; a constant 30 fps output plays everywhere.
            ProcessStartInfo info = new ProcessStartInfo(Ffmpeg())
            {
                Arguments = $"-y -loglevel error -f rawvideo -pix_fmt rgba -s {width}x{height} -framerate {fps} -i - " +
                            $"-vf pad=ceil(iw/2)*2:ceil(ih/2)*2 -c:v libx264 -preset veryfast -crf 26 -pix_fmt yuv420p -r 30 \"{_video}\"",
                RedirectStandardInput = true,
                RedirectStandardError = false,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            _ffmpeg = Process.Start(info);
            _queue = new BlockingCollection<byte[]>(8);
            Stream stdin = _ffmpeg.StandardInput.BaseStream;
            BlockingCollection<byte[]> queue = _queue;
            _writer = new Thread(() =>
            {
                try
                {
                    foreach (byte[] frame in queue.GetConsumingEnumerable())
                    {
                        stdin.Write(frame, 0, frame.Length);
                    }

                    stdin.Flush();
                }
                catch (IOException)
                {
                    // ffmpeg exited
                }
            })
            { IsBackground = true, Name = "FrameRecorder.ffmpeg" };
            _writer.Start();
        }

        /// <summary>ffmpeg from NAVREY_FFMPEG, Homebrew's place, or the PATH.</summary>
        private static string Ffmpeg()
        {
            string fromEnv = Environment.GetEnvironmentVariable("NAVREY_FFMPEG");

            if (!string.IsNullOrEmpty(fromEnv))
            {
                return fromEnv;
            }

            foreach (string candidate in new[] { "/opt/homebrew/bin/ffmpeg", "/usr/local/bin/ffmpeg", "/usr/bin/ffmpeg" })
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return "ffmpeg";
        }
    }
}
