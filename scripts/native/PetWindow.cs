using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Media;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace RecyclePet {
    public sealed class PetWindow : Window {
        readonly Canvas canvas = new Canvas(), actor = new Canvas();
        readonly Image sprite = new Image(), fileImage = new Image();
        readonly Border prop = new Border(), speech = new Border(), modeBadge = new Border();
        readonly TextBlock words = new TextBlock(), modeWords = new TextBlock(), fileTag = new TextBlock(), batchTag = new TextBlock();
        readonly DispatcherTimer timer = new DispatcherTimer();
        readonly Dictionary<string, BitmapSource[]> sheets = new Dictionary<string, BitmapSource[]>();
        readonly ScaleTransform scale = new ScaleTransform(1, 1);
        readonly RotateTransform rotate = new RotateTransform(0);
        readonly TranslateTransform shift = new TranslateTransform(0, 0);
        readonly MenuItem realModeItem = new MenuItem(), muteItem = new MenuItem();
        readonly SoundPlayer sound, tokenSound;
        string skin = "maid"; bool busy, working, ready, demoMode, demoPlayback, closed, committing;
        int frame, stage = -1; double elapsed, voiceStartedAt;
        bool nextSwallow, nextDemoSwallow, swallowing, explicitDemo, voicePlayed;
        Stopwatch clock = new Stopwatch();
        string[] pending = new string[0];
        const double SpriteSize = 320;
        readonly string assetRoot;
        IntPtr owner;
        // x, y, scale in the same sprite-sheet coordinate system as the web prototype.
        static readonly double[,] FrogAnchors = {{.50,.64,1},{.64,.45,1.35},{.51,.015,1.8},{.57,.76,1},{.83,.40,1},{.50,.66,1}};
        static readonly double[,] MaidAnchors = {{.50,.64,1},{.50,.66,1},{.47,.15,2.1},{.49,.84,1},{.80,.54,1},{.47,.70,1}};
        public PetWindow(string assetRoot, string initialSkin) {
            this.assetRoot = assetRoot; skin = initialSkin == "frog" ? "frog" : "maid";
            Title = "回收站桌宠"; Width = 225; Height = 255; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true; Background = Brushes.Transparent; ShowInTaskbar = false; Topmost = true;
            Rect work = SystemParameters.WorkArea; Left = Math.Max(work.Left, work.Right - Width - 16); Top = Math.Max(work.Top, work.Bottom - Height - 8);
            // Keep all animation coordinates in the original logical canvas; scale the whole scene together.
            canvas.Width = 450; canvas.Height = 510;
            Content = new Viewbox {Stretch = Stretch.Uniform, StretchDirection = StretchDirection.Both, Child = canvas}; AllowDrop = true;
            sheets["frog"] = LoadSheet(Path.Combine(assetRoot, "frog.png")); sheets["maid"] = LoadSheet(Path.Combine(assetRoot, "maid.png"));
            sheets["swallow"] = LoadSheet(Path.Combine(assetRoot, "maid-swallow.png"),32);
            actor.Width = actor.Height = SpriteSize; actor.RenderTransformOrigin = new Point(.5, .93);
            TransformGroup group = new TransformGroup(); group.Children.Add(scale); group.Children.Add(rotate); group.Children.Add(shift); actor.RenderTransform = group;
            Canvas.SetLeft(actor, 12); Canvas.SetTop(actor, 130); canvas.Children.Add(actor);
            sprite.Width = sprite.Height = SpriteSize; sprite.Stretch = Stretch.Fill; actor.Children.Add(sprite);
            ConfigureProp(); actor.Children.Add(prop); SetFrame(0, false);
            ConfigureSpeech(); canvas.Children.Add(speech); Canvas.SetLeft(speech, 14); Canvas.SetTop(speech, 12);
            TextBlock bin = new TextBlock {Text = "♻", FontFamily = new FontFamily("Segoe UI Symbol"), FontSize = 54, Foreground = new SolidColorBrush(Color.FromRgb(181, 217, 246)), TextAlignment = TextAlignment.Center};
            Border bucket = new Border {Width = 74, Height = 94, CornerRadius = new CornerRadius(9,9,19,19), BorderThickness = new Thickness(3), BorderBrush = new SolidColorBrush(Color.FromRgb(186, 223, 255)), Background = new SolidColorBrush(Color.FromArgb(220, 52, 103, 152)), Child = bin};
            Canvas.SetLeft(bucket, 360); Canvas.SetTop(bucket, 370); canvas.Children.Add(bucket);
            modeWords.FontSize = 12; modeWords.Foreground = Brushes.White; modeWords.TextAlignment = TextAlignment.Center;
            modeBadge.Child = modeWords; modeBadge.CornerRadius = new CornerRadius(11); modeBadge.Padding = new Thickness(12,5,12,5); modeBadge.MinWidth = 160;
            Canvas.SetLeft(modeBadge, 104); Canvas.SetTop(modeBadge, 475); canvas.Children.Add(modeBadge); SetModeBadge("正在验证真实回收…", false);
            BuildMenu();
            DragOver += OnDragOver; DragLeave += delegate {if (!busy && ready) Say("把文件拖给我吧。右键可以换角色。");}; Drop += OnDrop;
            MouseLeftButtonDown += delegate(object sender, System.Windows.Input.MouseButtonEventArgs e) {if (e.ClickCount == 2) { Begin(new string[0], true); e.Handled = true; } else if (!busy && e.LeftButton == System.Windows.Input.MouseButtonState.Pressed) { DragMove(); }};
            KeyDown += delegate(object sender, System.Windows.Input.KeyEventArgs e) {if (e.Key == System.Windows.Input.Key.Escape && busy && !committing) {Reset(); Say("这次不扔了，文件没有处理。");}};
            Closing += delegate(object sender, System.ComponentModel.CancelEventArgs e) {if (working) {e.Cancel = true; Say("系统正在处理，请稍等结果。");}};
            Closed += delegate {closed = true; timer.Stop(); StopVoices();};
            timer.Interval = TimeSpan.FromMilliseconds(16); timer.Tick += Tick; timer.Start();
            try {sound = new SoundPlayer(Path.Combine(assetRoot, "voice.wav")); sound.LoadAsync();} catch {}
            try {tokenSound = new SoundPlayer(Path.Combine(assetRoot, "token.wav")); tokenSound.LoadAsync();} catch {}
            Loaded += delegate {owner = new WindowInteropHelper(this).Handle; BeginProbe();};
        }
        static BitmapSource[] LoadSheet(string file, int inset = 0) {
            BitmapImage image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.UriSource = new Uri(file); image.EndInit(); image.Freeze();
            if (image.PixelWidth % 3 != 0 || image.PixelHeight % 2 != 0) throw new IOException("动作图集不是 3×2 网格：" + file);
            int w = image.PixelWidth / 3, h = image.PixelHeight / 2; BitmapSource[] frames = new BitmapSource[6];
            for (int i = 0; i < 6; i++) {CroppedBitmap crop = new CroppedBitmap(image, new Int32Rect((i % 3)*w+inset, (i/3)*h+inset, w-2*inset, h-2*inset)); crop.Freeze(); frames[i] = crop;}
            return frames;
        }
        void ConfigureSpeech() {
            words.FontFamily = new FontFamily("Microsoft YaHei UI"); words.FontSize = 15; words.FontWeight = FontWeights.SemiBold; words.TextWrapping = TextWrapping.Wrap; words.Foreground = new SolidColorBrush(Color.FromRgb(32, 47, 70)); words.TextAlignment = TextAlignment.Center;
            speech.Width = 394; speech.MinHeight = 48; speech.Padding = new Thickness(12,9,12,9); speech.CornerRadius = new CornerRadius(16); speech.Background = new SolidColorBrush(Color.FromArgb(247, 246, 249, 255)); speech.Child = words;
        }
        void ConfigureProp() {
            Grid grid = new Grid(); grid.RowDefinitions.Add(new RowDefinition()); grid.RowDefinitions.Add(new RowDefinition {Height = new GridLength(18)});
            fileImage.Width = fileImage.Height = 40; fileImage.Stretch = Stretch.Uniform; fileImage.Margin = new Thickness(3); grid.Children.Add(fileImage);
            fileTag.Text = "FILE"; fileTag.FontSize = 10; fileTag.FontWeight = FontWeights.Bold; fileTag.TextAlignment = TextAlignment.Center; fileTag.Foreground = new SolidColorBrush(Color.FromRgb(45, 87, 141)); Grid.SetRow(fileTag,1); grid.Children.Add(fileTag);
            batchTag.FontSize = 10; batchTag.Foreground = Brushes.White; batchTag.Background = Brushes.DarkSlateBlue; batchTag.HorizontalAlignment = HorizontalAlignment.Right; batchTag.VerticalAlignment = VerticalAlignment.Top; grid.Children.Add(batchTag);
            prop.Child = grid; prop.Width = 56; prop.Height = 70; prop.Background = Brushes.White; prop.BorderBrush = Brushes.LightSteelBlue; prop.BorderThickness = new Thickness(1); prop.CornerRadius = new CornerRadius(7); prop.Visibility = Visibility.Hidden; prop.RenderTransformOrigin = new Point(.5,.5);
        }
        void BuildMenu() {
            ContextMenu menu = new ContextMenu(); ContextMenu = menu;
            AddMenu(menu, "鲸鱼娘 / 大肥鱼（默认）", delegate {Switch("maid");}); AddMenu(menu, "奶蛙（备选）", delegate {Switch("frog");}); menu.Items.Add(new Separator());
            realModeItem.Header = "真实回收（自检通过后可用）"; realModeItem.IsCheckable = true; realModeItem.IsEnabled = false;
            realModeItem.Click += delegate {if (busy || !ready) {realModeItem.IsChecked = ready && !demoMode; return;} demoMode = !realModeItem.IsChecked; UpdateMode();}; menu.Items.Add(realModeItem);
            AddMenu(menu, "播放动作演示（不处理文件）", delegate {Begin(new string[0], true);});
            AddMenu(menu, "试听吞咽配音（不处理文件）", PreviewToken);
            muteItem.Header = "静音"; muteItem.IsCheckable = true; muteItem.Click += delegate {if (muteItem.IsChecked) StopVoices();}; menu.Items.Add(muteItem);
            AddMenu(menu, "打开 Windows 回收站", delegate {Process.Start(new ProcessStartInfo("explorer.exe", "shell:RecycleBinFolder") {UseShellExecute = true});});
            AddMenu(menu, "重新验证回收与恢复", delegate {if (!busy && !working) BeginProbe();});
            AddMenu(menu, "打开本机运行记录", delegate {Directory.CreateDirectory(Native.StateRoot); Process.Start(new ProcessStartInfo("explorer.exe", Native.StateRoot) {UseShellExecute = true});});
            menu.Items.Add(new Separator()); AddMenu(menu, "退出", delegate {Close();});
        }
        static void AddMenu(ContextMenu menu, string label, Action action) {MenuItem item = new MenuItem {Header = label}; item.Click += delegate {action();}; menu.Items.Add(item);}
        void Switch(string next) {if (busy || working) {Say("等这次结束就能换角色。"); return;} skin = next; SetFrame(0, false); UpdateMode(); Say(skin == "frog" ? "……给我拿好了呀。" : "交给我，我会好好拿着的～");}
        void Say(string text) {words.Text = busy && voicePlayed && !committing ? (swallowing ? "我要吃你的token了" : "我 Chovy！东西给我拿好了呀！") : text;}
        void StopVoices() {
            if (sound != null) try {sound.Stop();} catch {}
            if (tokenSound != null) try {tokenSound.Stop();} catch {}
        }
        void PreviewToken() {
            if (busy || working) return;
            StopVoices(); SetFrame(0, false); Say("我要吃你的token了");
            if (!muteItem.IsChecked && tokenSound != null) try {tokenSound.Play();} catch {}
        }
        void SetModeBadge(string text, bool real) {modeWords.Text = text; modeBadge.Background = new SolidColorBrush(real ? Color.FromArgb(235,38,116,89) : Color.FromArgb(235,88,88,110));}
        void UpdateMode() {realModeItem.IsChecked = ready && !demoMode; SetModeBadge(ready && !demoMode ? "真实回收 · 拖入即回收" : "动作演示 · 不处理文件", ready && !demoMode); modeWords.Text += skin == "maid" ? (nextSwallow ? " · 下次吞咽" : " · 下次砸文件") : " · 砸文件";}
        void SetFrame(int value, bool hold) {
            frame = value; sprite.Source = sheets[skin][value]; prop.Clip = null;
            if (!hold) {prop.Visibility = Visibility.Hidden; return;}
            if (prop.Parent != actor) {canvas.Children.Remove(prop); actor.Children.Add(prop);}
            double[,] a = skin == "frog" ? FrogAnchors : MaidAnchors;
            prop.Visibility = Visibility.Visible; prop.Opacity = 1; prop.RenderTransform = new ScaleTransform(a[value,2], a[value,2]);
            Canvas.SetLeft(prop, SpriteSize*a[value,0]-prop.Width/2); Canvas.SetTop(prop, SpriteSize*a[value,1]-prop.Height/2);
        }
        void OnDragOver(object sender, DragEventArgs e) {
            e.Handled = true; e.Effects = !busy && !working && ready && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            if (!busy && !working) Say(ready ? "松开吧，交给我！" : "先等回收自检完成。");
        }
        void OnDrop(object sender, DragEventArgs e) {
            e.Handled = true; if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            if (busy || working || !ready) {Say("先等当前操作完成，再拖一次。"); return;}
            e.Effects = DragDropEffects.Copy; // Consume paths only; the Shell backend performs the actual recycle later.
            string[] files = (string[])e.Data.GetData(DataFormats.FileDrop); Begin(files, false);
        }
        void LoadProp(string file) {
            fileImage.Source = null; fileTag.Text = "DEMO";
            if (String.IsNullOrEmpty(file)) return;
            fileTag.Text = Path.GetExtension(file).TrimStart('.').ToUpperInvariant(); if (fileTag.Text.Length > 6) fileTag.Text = "FILE";
            try {
                if (new [] {".png", ".jpg", ".jpeg", ".bmp", ".gif"}.Contains(Path.GetExtension(file).ToLowerInvariant())) {
                    BitmapImage image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.DecodePixelWidth = 96; image.UriSource = new Uri(file); image.EndInit(); image.Freeze(); fileImage.Source = image; return;
                }
            } catch {}
            try {
                SHFILEINFO info = new SHFILEINFO(); SHGetFileInfo(file,0,ref info,(uint)Marshal.SizeOf(typeof(SHFILEINFO)),0x100|0x1);
                if (info.hIcon != IntPtr.Zero) {try {BitmapSource icon = Imaging.CreateBitmapSourceFromHIcon(info.hIcon,Int32Rect.Empty,BitmapSizeOptions.FromWidthAndHeight(40,40)); icon.Freeze(); fileImage.Source = icon;} finally {DestroyIcon(info.hIcon);}}
            } catch {}
        }
        void Begin(string[] files, bool demonstration) {
            if (busy || working) return;
            if (!demonstration && !ready) {Say("先让回收自检通过。"); return;}
            try {
                if (!demonstration) {
                    if (files == null || files.Length == 0 || files.Length > 20) throw new IOException("每次拖入 1～20 个普通文件。");
                    files = files.Select(Native.Validate).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                }
                StopVoices();
                explicitDemo = demonstration; swallowing = skin == "maid" && (demonstration ? nextDemoSwallow : nextSwallow);
                voicePlayed = false; voiceStartedAt = 0;
                pending = files ?? new string[0]; demoPlayback = demonstration || demoMode; LoadProp(pending.Length == 0 ? "" : pending[0]); batchTag.Text = pending.Length > 1 ? "+"+(pending.Length-1) : "";
                busy = true; committing = false; stage = -1; elapsed = 0; clock.Restart();
            } catch (Exception e) {Say(e.Message);}
        }
        void Reset() {
            busy = false; committing = false; stage = -1; clock.Stop(); pending = new string[0];
            StopVoices();
            if (prop.Parent == canvas) {canvas.Children.Remove(prop); actor.Children.Add(prop);}
            scale.ScaleX = scale.ScaleY = 1; rotate.Angle = shift.Y = 0; SetFrame(0,false); UpdateMode();
        }
        void Tick(object sender, EventArgs e) {
            if (!busy) {shift.Y = working ? 0 : -Math.Sin(DateTime.Now.TimeOfDay.TotalSeconds*2)*2; return;}
            if (committing) return;
            elapsed = clock.Elapsed.TotalMilliseconds;
            if (elapsed >= 350 && !voicePlayed) {
                voicePlayed = true; voiceStartedAt = elapsed;
                Say(swallowing ? "我要吃你的token了" : "我 Chovy！东西给我拿好了呀！");
                SoundPlayer clip = swallowing ? tokenSound : sound;
                if (!muteItem.IsChecked && clip != null) try {clip.Play();} catch {}
            }
            // Anchor the animation clock to actual voice start, including delayed UI ticks.
            if (voicePlayed) elapsed -= voiceStartedAt-350;
            if (swallowing) {TickSwallow(); return;}
            int target = elapsed < 350 ? 0 : elapsed < 1450 ? 1 : elapsed < 2150 ? 2 : elapsed < 2440 ? 3 : elapsed < 3150 ? 4 : elapsed < 3610 ? 5 : 6;
            if (target != stage) {
                stage = target;
                if (stage == 0) {SetFrame(1,true); Say("拿过来，我看看。");}
                if (stage == 1) {SetFrame(2,true); Say("我 Chovy！东西给我拿好了呀！");}
                if (stage == 2 || stage == 4) {SetFrame(3,true); Say("我 Chovy！东西给我拿好了呀！");}
                if (stage == 3) SetFrame(2,true);
                if (stage == 5) {
                    SetFrame(4,true); Point p = prop.TranslatePoint(new Point(prop.Width/2,prop.Height/2),canvas); actor.Children.Remove(prop); canvas.Children.Add(prop); prop.RenderTransform = Transform.Identity;
                    flightFrom = p;
                }
                if (stage == 6) {prop.Visibility = Visibility.Hidden; SetFrame(5,false); Commit(); return;}
            }
            scale.ScaleX = stage == 2 || stage == 4 ? 1.045 : 1; scale.ScaleY = stage == 2 || stage == 4 ? .955 : 1;
            rotate.Angle = stage == 1 || stage == 3 ? -3 : stage == 2 || stage == 4 ? 2 : 0;
            shift.Y = stage == 2 || stage == 4 ? 5 : stage == 1 || stage == 3 ? -6 : 0;
            if (stage == 5) {double t = Math.Max(0,Math.Min(1,(elapsed-3150)/460)); Canvas.SetLeft(prop,flightFrom.X+(397-flightFrom.X)*t-prop.Width/2); Canvas.SetTop(prop,flightFrom.Y+(420-flightFrom.Y)*t-Math.Sin(t*Math.PI)*80-prop.Height/2); prop.RenderTransform = new RotateTransform(t*300); prop.Opacity = 1-t*.95;}
        }
        void SetSwallowFrame(int value, bool holding) {
            frame = value; sprite.Source = sheets["swallow"][value]; prop.Clip = null;
            if (prop.Parent != actor) {canvas.Children.Remove(prop); actor.Children.Add(prop);}
            prop.Visibility = holding ? Visibility.Visible : Visibility.Hidden;
            prop.Opacity = 1; prop.RenderTransform = Transform.Identity;
            Canvas.SetLeft(prop, SpriteSize*.50-prop.Width/2); Canvas.SetTop(prop, SpriteSize*.64-prop.Height/2);
        }
        void TickSwallow() {
            int target = elapsed < 350 ? 0 : elapsed < 1100 ? 1 : elapsed < 2000 ? 2 : elapsed < 2450 ? 3 : elapsed < 2850 ? 4 : elapsed < 3250 ? 5 : 6;
            if (target != stage) {
                stage = target;
                if (stage < 6) SetSwallowFrame(stage, stage < 3);
                if (stage == 0) Say("拿过来，我看看。");
                if (stage == 6) {prop.Visibility = Visibility.Hidden; Commit(); return;}
            }
            if (stage == 1) {
                double t = Math.Max(0,Math.Min(1,(elapsed-350)/750));
                Canvas.SetTop(prop, SpriteSize*(.64+(.53+prop.Height/(2*SpriteSize)-.64)*t)-prop.Height/2);
            }
            if (stage == 2) {
                double t = Math.Max(0,Math.Min(1,(elapsed-1100)/900));
                double size = 1-.70*t;
                Canvas.SetTop(prop, SpriteSize*.53+(.5-t)*prop.Height*size-prop.Height/2);
                prop.RenderTransform = new ScaleTransform(size,size);
                prop.Clip = new RectangleGeometry(new Rect(0,prop.Height*t,prop.Width,prop.Height*(1-t)));
            }
            double chew = stage == 3 ? Math.Sin((elapsed-2000)/70)*.025 : 0;
            scale.ScaleX = 1+chew; scale.ScaleY = 1-chew;
            rotate.Angle = stage == 2 ? Math.Sin(elapsed/65)*1.5 : 0;
            shift.Y = stage == 4 ? Math.Sin((elapsed-2450)/400*Math.PI)*-5 : 0;
        }
        Point flightFrom;
        void BeginProbe() {
            if (working || busy) return; working = true; ready = false; realModeItem.IsEnabled = false; SetModeBadge("正在验证回收与恢复…",false); Say("我先用自己新建的小文件试一下回收与恢复。");
            Thread thread = new Thread(delegate() {
                ProbeResult probe;
                try {probe = Native.Probe(owner);} catch (Exception e) {probe = new ProbeResult {Error=e.Message};}
                Dispatcher.BeginInvoke(new Action(delegate {if (closed) return; working = false; ready = probe.Passed; demoMode = !ready; realModeItem.IsEnabled = ready; UpdateMode(); Say(ready ? "回收和恢复自检通过，把文件拖给我吧！" : "自检未通过，只能看动作。右键打开运行记录："+probe.Error);}));
            }); thread.SetApartmentState(ApartmentState.STA); thread.Start();
        }
        void Commit() {
            committing = true;
            if (skin == "maid") {if (explicitDemo) nextDemoSwallow = !nextDemoSwallow; else nextSwallow = !nextSwallow;}
            UpdateMode();
            if (demoPlayback) {Say("这次只表演，文件没有处理。"); FinishSoon(); return;}
            working = true; string[] files = pending.ToArray(); Say("正在移入 Windows 回收站…");
            Thread thread = new Thread(delegate() {
                List<Receipt> receipts = new List<Receipt>(); string logError = "";
                foreach (string path in files) {Receipt receipt = Native.Recycle(path,owner); receipts.Add(receipt); try {Native.AppendReceipt(receipt);} catch (Exception e) {logError = e.Message;}}
                Dispatcher.BeginInvoke(new Action(delegate {
                    if (closed) return; working = false; int good = receipts.Count(r=>r.Success), bad = receipts.Count-good;
                    Say(bad == 0 ? "已进入系统回收站："+good+" 个文件。右键可打开查看。" : "已回收 "+good+" 个，未完成 "+bad+" 个。"+receipts.First(r=>!r.Success).Error);
                    if (!String.IsNullOrEmpty(logError)) Say(words.Text+"（运行记录未写入）"); FinishSoon();
                }));
            }); thread.SetApartmentState(ApartmentState.STA); thread.Start();
        }
        void FinishSoon() {DispatcherTimer end = new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(900)}; end.Tick += delegate {end.Stop(); if (!closed) Reset();}; end.Start();}
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
        struct SHFILEINFO {public IntPtr hIcon; public int iIcon; public uint attrs; [MarshalAs(UnmanagedType.ByValTStr,SizeConst=260)] public string display; [MarshalAs(UnmanagedType.ByValTStr,SizeConst=80)] public string type;}
        [DllImport("shell32.dll",CharSet=CharSet.Unicode)] static extern IntPtr SHGetFileInfo(string path,uint attrs,ref SHFILEINFO info,uint size,uint flags);
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr icon);
        public static void Run(string root,string skin) {new PetWindow(root,skin).ShowDialog();}
    }
}
