using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Varta.Core;

namespace Varta.Platform
{
    /// <summary>Відтворення звуку через системний MCI (winmm) — працює з MP3/WAV без сторонніх бібліотек.</summary>
    internal static class Mci
    {
        [DllImport("winmm.dll", EntryPoint = "mciSendStringW", CharSet = CharSet.Unicode)]
        static extern int mciSendString(string command, StringBuilder returnString, int returnLength, IntPtr callback);

        [DllImport("winmm.dll", EntryPoint = "mciGetErrorStringW", CharSet = CharSet.Unicode)]
        static extern bool mciGetErrorString(int error, StringBuilder buffer, int length);

        static int _n;

        static int Send(string cmd, out string ret)
        {
            var sb = new StringBuilder(256);
            int err = mciSendString(cmd, sb, sb.Capacity, IntPtr.Zero);
            ret = sb.ToString();
            return err;
        }

        static string ErrorText(int err)
        {
            var sb = new StringBuilder(256);
            return mciGetErrorString(err, sb, sb.Capacity) ? sb.ToString() : "код " + err;
        }

        static string TypeFor(string file)
        {
            string ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext == ".mp3" || ext == ".mp2") return " type mpegvideo";
            if (ext == ".wav") return " type waveaudio";
            return "";
        }

        /// <summary>Перевіряє, що файл відкривається, і повертає тривалість у мс.</summary>
        public static string Probe(string file, out int lengthMs)
        {
            lengthMs = 0;
            string alias = "vprobe" + Interlocked.Increment(ref _n);
            string r;
            int err;
            try { err = Send("open \"" + file + "\"" + TypeFor(file) + " alias " + alias, out r); }
            catch (Exception ex) { return "звукова підсистема недоступна (" + ex.Message + ")"; }
            if (err != 0) return "файл не відкривається (" + ErrorText(err) + ")";
            try
            {
                Send("set " + alias + " time format milliseconds", out r);
                if (Send("status " + alias + " length", out r) == 0) int.TryParse(r, out lengthMs);
                return null;
            }
            finally { Send("close " + alias, out r); }
        }

        /// <summary>Відтворює файл до кінця або до скасування. null = успіх, інакше текст помилки.</summary>
        public static string PlayBlocking(string file, int volume1000, Func<bool> cancelled, Action tick)
        {
            string alias = "varta" + Interlocked.Increment(ref _n);
            string r;
            int err;
            try { err = Send("open \"" + file + "\"" + TypeFor(file) + " alias " + alias, out r); }
            catch (Exception ex) { return "звукова підсистема недоступна (" + ex.Message + ")"; }
            if (err != 0) return "не вдалося відкрити файл (" + ErrorText(err) + ")";
            try
            {
                Send("set " + alias + " time format milliseconds", out r);
                Send("setaudio " + alias + " volume to " + volume1000, out r);
                int length = 0;
                if (Send("status " + alias + " length", out r) == 0) int.TryParse(r, out length);
                err = Send("play " + alias, out r);
                if (err != 0) return "не вдалося почати відтворення (" + ErrorText(err) + ")";

                var sw = Stopwatch.StartNew();
                while (true)
                {
                    if (cancelled()) break;
                    Thread.Sleep(80);
                    if (tick != null) tick();
                    string mode;
                    if (Send("status " + alias + " mode", out mode) == 0 && mode == "stopped" && sw.ElapsedMilliseconds > 400) break;
                    if (length > 0 && sw.ElapsedMilliseconds > length + 5000) break;
                    if (sw.ElapsedMilliseconds > 15 * 60 * 1000) break;
                }
                return null;
            }
            finally
            {
                Send("stop " + alias, out r);
                Send("close " + alias, out r);
            }
        }
    }

    public sealed class AnnounceRequest
    {
        public string File = "";
        public int Repeat = 1;
        public int GapSeconds = 3;
        public int VolumePercent = 100;
        public bool Duck = true;
        public float KeepRatio = 0.1f;
        public bool Boost = true;
        public float MinMaster = 0.7f;
        public bool Unmute = true;
        public string Tag = "";
    }

    /// <summary>
    /// Черга оголошень на окремому потоці: нове оголошення перериває попереднє (завжди актуальне найновіше),
    /// на час відтворення музика приглушується, після — гучність повертається.
    /// </summary>
    public sealed class AudioService : IDisposable
    {
        readonly object _l = new object();
        readonly AutoResetEvent _wake = new AutoResetEvent(false);
        readonly AudioDucker _ducker = new AudioDucker();
        readonly Thread _thread;
        AnnounceRequest _pending;
        bool _cancel;
        volatile bool _stop;

        public event Action<string> Problem;
        public bool IsPlaying { get; private set; }

        public AudioService()
        {
            _thread = new Thread(Loop) { IsBackground = true, Name = "Varta.Audio" };
            _thread.SetApartmentState(ApartmentState.MTA);
            _thread.Start();
        }

        public void Announce(AnnounceRequest r)
        {
            lock (_l) { _pending = r; _cancel = true; }
            _wake.Set();
        }

        public void Stop()
        {
            lock (_l) { _pending = null; _cancel = true; }
        }

        bool Cancelled() { lock (_l) return _cancel || _stop; }
        bool HasPending() { lock (_l) return _pending != null; }

        void Raise(string msg)
        {
            Log.Warn(msg);
            try { var h = Problem; if (h != null) h(msg); } catch { }
        }

        void Loop()
        {
            _ducker.RecoverIfNeeded();
            while (!_stop)
            {
                _wake.WaitOne(1000);
                if (_stop) break;
                AnnounceRequest r;
                lock (_l) { r = _pending; _pending = null; _cancel = false; }
                if (r == null) continue;
                try { Run(r); }
                catch (Exception ex) { Log.Error("Збій відтворення", ex); try { _ducker.Restore(); } catch { } }
                finally { IsPlaying = false; }
            }
            try { _ducker.Restore(); } catch { }
        }

        void Run(AnnounceRequest r)
        {
            IsPlaying = true;
            if (r.Duck)
            {
                string e = _ducker.Duck(r.KeepRatio, r.Boost, r.MinMaster, r.Unmute);
                if (e != null) Raise("Не вдалося приглушити музику: " + e);
            }

            DateTime lastDuck = DateTime.UtcNow;
            Action tick = delegate
            {
                if (r.Duck && (DateTime.UtcNow - lastDuck).TotalMilliseconds > 1000)
                {
                    lastDuck = DateTime.UtcNow;
                    _ducker.Duck(r.KeepRatio, r.Boost, r.MinMaster, r.Unmute);
                }
            };

            string path = r.File;
            bool exists = !string.IsNullOrEmpty(path) && File.Exists(path);
            if (!exists)
            {
                Raise("Файл оголошення не знайдено: " + (string.IsNullOrEmpty(path) ? "(не вказано)" : path) + ". Відтворено резервний сигнал.");
                Beep();
            }
            else
            {
                for (int i = 0; i < Math.Max(1, r.Repeat) && !Cancelled(); i++)
                {
                    if (i > 0 && !SleepCancellable(r.GapSeconds * 1000, tick)) break;
                    string err = Mci.PlayBlocking(path, Math.Max(0, Math.Min(1000, r.VolumePercent * 10)), Cancelled, tick);
                    if (err != null)
                    {
                        Raise("Помилка відтворення «" + Path.GetFileName(path) + "»: " + err + ". Відтворено резервний сигнал.");
                        Beep();
                        break;
                    }
                }
            }

            if (!HasPending())
            {
                Thread.Sleep(350);
                _ducker.Restore();
            }
        }

        bool SleepCancellable(int ms, Action tick)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
            {
                if (Cancelled()) return false;
                Thread.Sleep(100);
                if (tick != null) tick();
            }
            return true;
        }

        void Beep()
        {
            try
            {
                for (int i = 0; i < 3 && !Cancelled(); i++)
                {
                    System.Media.SystemSounds.Exclamation.Play();
                    if (!SleepCancellable(700, null)) break;
                }
            }
            catch { }
        }

        /// <summary>Перевірка файлу без відтворення: null = ок.</summary>
        public static string CheckFile(string fullPath, out int lengthMs)
        {
            lengthMs = 0;
            if (string.IsNullOrWhiteSpace(fullPath)) return "файл не вказано";
            if (!File.Exists(fullPath)) return "файл не знайдено";
            try { if (new FileInfo(fullPath).Length == 0) return "файл порожній"; } catch { }
            return Mci.Probe(fullPath, out lengthMs);
        }

        public void Dispose()
        {
            _stop = true;
            lock (_l) { _cancel = true; }
            _wake.Set();
            try { _thread.Join(2000); } catch { }
        }
    }
}
