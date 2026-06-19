using System.IO;
using System.Windows;
using FFMpegCore;

namespace LosslessCutter
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Look for ffmpeg binaries next to the exe first, then fall back to system PATH
            var localBin = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg");
            if (Directory.Exists(localBin))
                GlobalFFOptions.Configure(new FFOptions { BinaryFolder = localBin });
        }
    }
}
