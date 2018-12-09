using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using z80emu;

namespace z80view
{
    public class EmulatorViewModel : Avalonia.Diagnostics.ViewModels.ViewModelBase
    {
        private readonly IUIInvalidator invalidate;

        private readonly IAskUserFile askFile;

        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();

        private readonly AutoResetEvent nextFrame = new AutoResetEvent(false);

        private readonly AutoResetEvent nextBeep = new AutoResetEvent(false);

        private readonly Thread drawingThread;

        private readonly Thread emulatorThread;

        private readonly Thread soundThread;

        private readonly Emulator emulator;

        private readonly KeyMapping keyMapping;

        private FrameEventArgs frame;

        private BeepEventArgs beep;

        public EmulatorViewModel(IUIInvalidator invalidate, IAskUserFile askFile)
        {
            this.invalidate = invalidate;
            this.askFile = askFile;

            this.keyMapping = new KeyMapping();
            this.emulator = new Emulator();

            this.Bitmap = new WritableBitmap(352, 312);
            this.DumpCommand = new ActionCommand(Dump);
            this.LoadCommand = new ActionCommand(Load);

            this.emulatorThread = new Thread(RunEmulator);
            this.emulatorThread.Start();

            this.drawingThread = new Thread(DrawScreen);
            this.drawingThread.Start();

            this.soundThread = new Thread(BeepSound);
            this.soundThread.Start();
        }

        public ICommand DumpCommand { get; }

        public ICommand LoadCommand { get; }

        public WritableBitmap Bitmap { get; }

        public string FPS {get;set;}

        public int Delay {get;set;}
        
        public void Stop()
        {
            this.cancellation.Cancel();
            this.emulatorThread.Join();
            this.drawingThread.Join();
            this.soundThread.Join();
            this.cancellation.Dispose();
            this.nextFrame.Dispose();
            this.nextBeep.Dispose();
        }

        public void KeyDown(Avalonia.Input.KeyEventArgs args)
        {
            var k = this.keyMapping.Map(args);
            if (k != Key.None)
            {
                this.emulator.KeyDown(k);
            }
        }

        public void KeyUp(Avalonia.Input.KeyEventArgs args)
        {
            var k = this.keyMapping.Map(args);
            if (k != Key.None)
            {
                this.emulator.KeyUp(k);
            }
        }

        private void Dump()
        {
            this.emulator.Dump();
        }

        private async void Load()
        {
            var file = await this.askFile.AskFile();
            if (file != null)
            {
                this.emulator.Load(file);
            }
        }

        private void RunEmulator()
        {
            this.emulator.NextFrame += args =>
            {
                Interlocked.Exchange(ref this.frame, args);
                this.nextFrame.Set();
            };

            this.emulator.NextBeep += args =>
            {
                Interlocked.Exchange(ref this.beep, args);
                this.nextBeep.Set();
            };

            this.emulator.Run(() => this.Delay, this.cancellation.Token);
        }

        private void BeepSound()
        {
            try
            {
                while (!this.cancellation.IsCancellationRequested)
                {
                    nextBeep.WaitOne(1000);
                    var beepCopy = Interlocked.Exchange(ref this.beep, null);
                    if (beepCopy == null || beepCopy.Duration <= 0)
                    {
                        continue;
                    }

                    Console.Beep(beepCopy.Frequency, beepCopy.Duration);
                }
            }
            catch(OperationCanceledException)
            {}
        }

        private unsafe void DrawScreen()
        {
            try
            {
                var previousFrameTimestamp = DateTime.Now;
                while (!this.cancellation.IsCancellationRequested)
                {
                    nextFrame.WaitOne(1000);
                    if (frame == null)
                    {
                        continue;
                    }
                    
                    var n = this.frame.FrameNumber;
                    if (n % 100 == 0)
                    {
                        // every 100 frames, measure how long did it take to draw it
                        var newTimestamp = DateTime.Now;
                        var timeSpent = newTimestamp - previousFrameTimestamp;
                        previousFrameTimestamp = newTimestamp;

                        // 100 frames / {timeSpent}
                        var fps = (int)(100 / timeSpent.TotalSeconds);
                        this.FPS = "FPS:" + fps.ToString("0000");
                        this.RaisePropertyChanged(nameof(FPS));
                    }

                    var bmp = Bitmap;
                    using (var buf = bmp.Lock())
                    {
                        var pal = frame.Palette;
                        var src = frame.Frame;
                        var dst = (uint*) buf.Address;
                        switch (buf.Format)
                        {
                            case PixelFormat.Rgba8888:
                                for (int i = 0; i < src.Length; ++i)
                                {
                                    var c = pal[src[i]];
                                    var rgba = (uint)(c.B << 16 | c.G << 8 | c.R) | 0xFF000000;
                                    dst[i] = rgba;
                                }
                                break;
                            case PixelFormat.Bgra8888:
                                for (int i = 0; i < src.Length; ++i)
                                {
                                    var c = pal[src[i]];
                                    var rgba = (uint)(c.R << 16 | c.G << 8 | c.B) | 0xFF000000;
                                    dst[i] = rgba;
                                }
                                break;
                            default:
                                throw new NotImplementedException(buf.Format.ToString());
                        }
                    }

                    this.invalidate.Invalidate().Wait(this.cancellation.Token);
                }
            }
            catch(OperationCanceledException)
            {}
        }   
    }
}
