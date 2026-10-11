using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ToutCompteFait.Dialogs;

namespace ToutCompteFait;

/// <summary>Diagnostic : captures des écrans de premier lancement (<c>--firstrunshots &lt;dossier&gt;</c>).</summary>
public partial class App
{
    private static async Task FirstRunShotsAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        Settings = ToutCompteFait.Core.Services.AppSettings.Load();
        Target = Settings.ResolvedTarget;
        async Task Shot(Window w, string name)
        {
            w.WindowStartupLocation = WindowStartupLocation.Manual;
            w.Left = 40;
            w.Top = 40;
            w.Show();
            await Task.Delay(400);
            w.UpdateLayout();
            var bmp = new RenderTargetBitmap((int)Math.Ceiling(w.ActualWidth), (int)Math.Ceiling(w.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bmp.Render(w);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using var fs = File.Create(Path.Combine(dir, name));
            enc.Save(fs);
            w.Close();
        }
        await Shot(new FirstRunWindow(Settings.ResolvedDatabasePath), "premier-lancement.png");
        await Shot(new DatabaseServerWindow(firstRun: true), "premier-lancement-serveur.png");
    }
}