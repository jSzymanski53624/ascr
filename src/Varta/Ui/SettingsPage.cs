using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Varta.Core;
using Varta.Platform;

namespace Varta.Ui
{
    public abstract class PageBase : UserControl
    {
        public abstract string PageTitle { get; }
        public abstract string PageSubtitle { get; }
        public virtual bool IsSettingsPage { get { return false; } }
        public virtual void OnShown() { }

        protected PageBase()
        {
            Dock = DockStyle.Fill;
            BackColor = Theme.Bg;
            Font = Theme.F(9.5f);
        }
    }

    /// <summary>Сторінка налаштувань: набір карток; кожен елемент керування прив'язаний до поля AppSettings.</summary>
    public abstract class SettingsPage : PageBase
    {
        protected readonly AppController C;
        protected readonly FlowLayoutPanel Flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(24, 20, 24, 20),
            BackColor = Theme.Bg
        };

        readonly List<Action<AppSettings>> _load = new List<Action<AppSettings>>();
        readonly List<Action<AppSettings>> _save = new List<Action<AppSettings>>();
        bool _loading;

        public event Action Changed;
        public override bool IsSettingsPage { get { return true; } }

        protected SettingsPage(AppController c)
        {
            C = c;
            Controls.Add(Flow);
            Flow.Resize += delegate { Relayout(); };
            Flow.ClientSizeChanged += delegate { Relayout(); };
            Flow.Layout += delegate { Relayout(); };
        }

        bool _relayout;

        void Relayout()
        {
            if (_relayout) return;
            _relayout = true;
            try
            {
                int w = UiKit.StackWidth(Flow, Flow.Padding.Horizontal);
                foreach (Control ctl in Flow.Controls) if (ctl.Width != w) ctl.Width = w;
            }
            finally { _relayout = false; }
        }

        public override void OnShown() { Relayout(); }

        public void PlayPreview(string stored) { C.PreviewSound(stored); }

        public void Bind(Action<AppSettings> load, Action<AppSettings> save)
        {
            _load.Add(load);
            _save.Add(save);
        }

        public void MarkChanged()
        {
            if (_loading) return;
            var h = Changed;
            if (h != null) h();
        }

        public void LoadFrom(AppSettings s)
        {
            _loading = true;
            try { foreach (var a in _load) a(s); }
            finally { _loading = false; }
        }

        public void SaveTo(AppSettings s)
        {
            foreach (var a in _save) a(s);
        }

        /// <summary>Дії, що не належать до AppSettings (наприклад, автозапуск у реєстрі).</summary>
        public virtual string ApplyExtras() { return null; }

        protected CardBuilder NewCard(string title, string desc)
        {
            var b = new CardBuilder(this, title, desc);
            Flow.Controls.Add(b.Card);
            return b;
        }
    }

    public sealed class CardBuilder
    {
        const int DesignW = 700, LabelX = 20, LabelW = 250, CtlX = 290;
        readonly SettingsPage _page;
        public readonly Card Card;
        readonly List<Control> _stretch = new List<Control>();
        int _y;

        public CardBuilder(SettingsPage page, string title, string desc)
        {
            _page = page;
            Card = new Card { Width = DesignW, Height = 120, Margin = new Padding(0, 0, 0, 16) };
            Card.SizeChanged += delegate { ApplyWidths(); };
            Label t = UiKit.Lbl(title, 12f, true, Theme.Navy);
            t.Location = new Point(LabelX, 16);
            Card.Controls.Add(t);
            _y = 16 + 32;
            if (!string.IsNullOrEmpty(desc))
            {
                Label d = Hint(desc, LabelX, DesignW - 2 * LabelX);
                _y += d.Height + 12;
            }
            else _y += 4;
        }

        public void Finish() { Card.Height = _y + 8; ApplyWidths(); }

        /// <summary>Розтягує елементи на ширину картки (без Anchor — однакова поведінка на всіх системах).</summary>
        void ApplyWidths()
        {
            int w = Card.ClientSize.Width;
            foreach (Control c in _stretch)
            {
                int nw = Math.Max(160, w - c.Left - LabelX);
                if (c.Width != nw) c.Width = nw;
            }
        }

        void Stretch(Control c) { _stretch.Add(c); }

        Label Hint(string text, int x, int width)
        {
            var l = new Label { Text = text, AutoSize = false, ForeColor = Theme.Muted, Font = Theme.F(9f), BackColor = Color.Transparent };
            int h = UiKit.FitHeight(text, l.Font, width);
            l.SetBounds(x, _y, width, h);
            Card.Controls.Add(l);
            Stretch(l);
            return l;
        }

        void Row(string label, Control ctl, int w, int h, string hint)
        {
            if (!string.IsNullOrEmpty(label))
            {
                var lb = new Label
                {
                    Text = label, AutoSize = false, AutoEllipsis = true, Font = Theme.F(9.75f), ForeColor = Theme.Text,
                    BackColor = Color.Transparent, TextAlign = ContentAlignment.MiddleLeft
                };
                lb.SetBounds(LabelX, _y, LabelW, Math.Min(h, 28));
                Card.Controls.Add(lb);
            }
            ctl.SetBounds(CtlX, _y, w > 0 ? w : DesignW - CtlX - LabelX, h);
            Card.Controls.Add(ctl);
            if (w <= 0) Stretch(ctl);
            _y += h;
            if (!string.IsNullOrEmpty(hint)) { _y += 3; _y += Hint(hint, CtlX, DesignW - CtlX - LabelX).Height; }
            _y += 12;
        }

        public CheckBox Check(string text, Func<AppSettings, bool> get, Action<AppSettings, bool> set, string hint)
        {
            var cb = new CheckBox { Text = text, AutoSize = false, Font = Theme.F(9.75f), ForeColor = Theme.Text, BackColor = Color.Transparent };
            cb.SetBounds(LabelX, _y, DesignW - 2 * LabelX, 26);
            Card.Controls.Add(cb);
            Stretch(cb);
            _y += 28;
            if (!string.IsNullOrEmpty(hint)) { _y += Hint(hint, LabelX + 24, DesignW - 2 * LabelX - 24).Height + 2; }
            _y += 8;
            cb.CheckedChanged += delegate { _page.MarkChanged(); };
            _page.Bind(s => cb.Checked = get(s), s => set(s, cb.Checked));
            return cb;
        }

        public NumericUpDown Num(string label, int min, int max, Func<AppSettings, int> get, Action<AppSettings, int> set, string unit, string hint)
        {
            var n = new NumericUpDown { Minimum = min, Maximum = max, Font = Theme.F(9.75f), TextAlign = HorizontalAlignment.Right };
            if (!string.IsNullOrEmpty(unit))
            {
                Label u = UiKit.Lbl(unit, 9.5f, false, Theme.Muted);
                u.Location = new Point(CtlX + 100, _y + 5);
                Card.Controls.Add(u);
            }
            Row(label, n, 90, 28, hint);
            n.ValueChanged += delegate { _page.MarkChanged(); };
            _page.Bind(s =>
            {
                n.Value = Math.Max(min, Math.Min(max, get(s)));
                n.Text = n.Value.ToString(System.Globalization.CultureInfo.CurrentCulture);
            }, s => set(s, (int)n.Value));
            return n;
        }

        public TextBox Txt(string label, Func<AppSettings, string> get, Action<AppSettings, string> set, string hint, bool password, int lines)
        {
            var t = new TextBox
            {
                Font = Theme.F(9.75f),
                BorderStyle = BorderStyle.FixedSingle,
                Multiline = lines > 1,
                ScrollBars = lines > 1 ? ScrollBars.Vertical : ScrollBars.None,
                UseSystemPasswordChar = password,
                AcceptsReturn = lines > 1
            };
            Row(label, t, 0, lines > 1 ? lines * 20 + 10 : 28, hint);
            t.TextChanged += delegate { _page.MarkChanged(); };
            _page.Bind(s => t.Text = get(s) ?? "", s => set(s, t.Text));
            return t;
        }

        public DateTimePicker Time(string label, Func<AppSettings, string> get, Action<AppSettings, string> set, string hint)
        {
            var d = new DateTimePicker { Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm", ShowUpDown = true, Font = Theme.F(9.75f) };
            Row(label, d, 90, 28, hint);
            d.ValueChanged += delegate { _page.MarkChanged(); };
            _page.Bind(s =>
            {
                TimeSpan t;
                Schedule.TryParseTime(get(s), out t);
                d.Value = DateTime.Today.Add(t);
            }, s => set(s, d.Value.ToString("HH:mm")));
            return d;
        }

        public void Days(string label, Func<AppSettings, string> get, Action<AppSettings, string> set)
        {
            var flow = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = true, BackColor = Color.Transparent, Margin = new Padding(0) };
            string[] names = { "Пн", "Вт", "Ср", "Чт", "Пт", "Сб", "Нд" };
            var boxes = new List<CheckBox>();
            for (int i = 0; i < 7; i++)
            {
                var cb = new CheckBox { Text = names[i], AutoSize = true, Margin = new Padding(0, 4, 12, 0), Font = Theme.F(9.75f) };
                cb.CheckedChanged += delegate { _page.MarkChanged(); };
                boxes.Add(cb);
                flow.Controls.Add(cb);
            }
            Row(label, flow, 0, 30, null);
            _page.Bind(s =>
            {
                string v = get(s);
                for (int i = 0; i < 7; i++) boxes[i].Checked = Schedule.DayEnabled(v, (DayOfWeek)((i + 1) % 7));
            }, s =>
            {
                var l = new List<string>();
                for (int i = 0; i < 7; i++) if (boxes[i].Checked) l.Add(Schedule.AllDays[i]);
                set(s, string.Join(",", l.ToArray()));
            });
        }

        /// <summary>Рядок вибору звукового файлу: шлях, «Обрати…», «Слухати», «Стандартний».</summary>
        public void SoundRow(string label, Func<AppSettings, string> get, Action<AppSettings, string> set, string defaultStored, string hint)
        {
            var host = new Panel { BackColor = Color.Transparent };
            var tb = new TextBox { ReadOnly = true, BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle, Font = Theme.F(9.5f), Dock = DockStyle.Top };
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Padding = new Padding(0, 8, 0, 0) };
            Button pick = UiKit.Btn("Обрати файл…", false);
            Button play = UiKit.Btn("▶ Прослухати", false);
            Button reset = UiKit.Btn("Стандартний", false);
            buttons.Controls.Add(pick);
            buttons.Controls.Add(play);
            buttons.Controls.Add(reset);
            host.Controls.Add(buttons);
            host.Controls.Add(tb);
            Row(label, host, 0, 76, hint);

            pick.Click += delegate
            {
                string stored = PickSound(tb.Text);
                if (stored != null) { tb.Text = stored; _page.MarkChanged(); }
            };
            play.Click += delegate { _page.PlayPreview(tb.Text); };
            reset.Click += delegate { tb.Text = defaultStored; _page.MarkChanged(); };
            _page.Bind(s => tb.Text = get(s) ?? "", s => set(s, tb.Text.Trim()));
        }

        static string PickSound(string current)
        {
            using (var d = new OpenFileDialog())
            {
                d.Title = "Оберіть звуковий файл оголошення";
                d.Filter = "Звукові файли (*.mp3;*.wav)|*.mp3;*.wav|Усі файли (*.*)|*.*";
                try { d.InitialDirectory = Directory.Exists(Paths.SoundsDir) ? Paths.SoundsDir : Paths.DataDir; } catch { }
                if (d.ShowDialog() != DialogResult.OK) return null;
                string chosen = d.FileName;
                string root = Paths.DataDir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string finalPath = chosen;
                if (!chosen.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        Directory.CreateDirectory(Paths.SoundsDir);
                        string name = Path.GetFileName(chosen);
                        string target = Path.Combine(Paths.SoundsDir, name);
                        int i = 2;
                        while (File.Exists(target) && !SameFile(chosen, target))
                            target = Path.Combine(Paths.SoundsDir, Path.GetFileNameWithoutExtension(name) + "_" + (i++) + Path.GetExtension(name));
                        if (!File.Exists(target)) File.Copy(chosen, target);
                        finalPath = target;
                        MessageBox.Show("Файл скопійовано в папку «sounds» поруч із програмою — так її можна переносити на інший комп'ютер разом із налаштуваннями.",
                            "ВАРТА", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show("Не вдалося скопіювати файл у папку «sounds»: " + ex.Message + "\nБуде використано оригінальний шлях.", "ВАРТА", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }
                string err;
                int len;
                err = AudioService.CheckFile(finalPath, out len);
                if (err != null)
                    MessageBox.Show("Увага: файл може не відтворюватися — " + err + ".", "ВАРТА", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return Paths.ToStored(finalPath);
            }
        }

        static bool SameFile(string a, string b)
        {
            try { return new FileInfo(a).Length == new FileInfo(b).Length && File.ReadAllBytes(a).Length == File.ReadAllBytes(b).Length && BytesEqual(a, b); }
            catch { return false; }
        }

        static bool BytesEqual(string a, string b)
        {
            byte[] x = File.ReadAllBytes(a), y = File.ReadAllBytes(b);
            if (x.Length != y.Length) return false;
            for (int i = 0; i < x.Length; i++) if (x[i] != y[i]) return false;
            return true;
        }

        public FlowLayoutPanel Buttons(params Button[] btns)
        {
            var f = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = true, BackColor = Color.Transparent };
            f.SetBounds(LabelX, _y, DesignW - 2 * LabelX, 40);
            foreach (Button b in btns) f.Controls.Add(b);
            Card.Controls.Add(f);
            Stretch(f);
            _y += 44;
            return f;
        }

        /// <summary>Поле для результату перевірки (✓ / ✗).</summary>
        public Label Result()
        {
            var l = new Label { AutoSize = false, Font = Theme.F(9.5f), ForeColor = Theme.Muted, BackColor = Color.Transparent };
            l.SetBounds(LabelX, _y, DesignW - 2 * LabelX, 52);
            Card.Controls.Add(l);
            Stretch(l);
            _y += 58;
            return l;
        }

        public static void ShowResult(Label l, string text)
        {
            l.Text = text;
            l.ForeColor = text.StartsWith("✓") ? Theme.Green : (text.StartsWith("✗") ? Theme.Red : Theme.Muted);
        }

        public Control Custom(Control c, int height)
        {
            c.SetBounds(LabelX, _y, DesignW - 2 * LabelX, height);
            Card.Controls.Add(c);
            Stretch(c);
            _y += height + 8;
            return c;
        }
    }
}
