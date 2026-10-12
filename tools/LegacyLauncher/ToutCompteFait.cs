// Lanceur de compatibilité « ToutCompteFait.exe » (publié avec la version Asso).
//
// Avant l'existence des versions, le programme s'appelait ToutCompteFait.exe : les mises à jour automatiques des anciennes
// installations vérifient la présence de ce fichier dans l'archive et le relancent après le remplacement ; les raccourcis
// (bureau, barre des tâches) pointent aussi dessus. Ce petit programme (.NET Framework 4, présent sur tout Windows) lance
// ToutCompteFaitAsso.exe, placé à côté, avec les mêmes arguments. Compilé par tools\Release.ps1 (csc.exe).
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Forms;

internal static class Launcher
{
    private const string Target = "ToutCompteFaitAsso.exe";

    [STAThread]
    private static int Main(string[] args)
    {
        var dir = AppDomain.CurrentDomain.BaseDirectory;
        var exe = Path.Combine(dir, Target);
        if (!File.Exists(exe))
        {
            MessageBox.Show(Target + " est introuvable dans :\n" + dir + "\n\nRéinstallez Tout Compte Fait Asso.",
                "Tout Compte Fait", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        var line = new StringBuilder();
        foreach (var a in args)
        {
            if (line.Length > 0) line.Append(' ');
            line.Append('"').Append(a.Replace("\"", "\\\"")).Append('"');
        }
        var psi = new ProcessStartInfo(exe, line.ToString()) { UseShellExecute = false, WorkingDirectory = dir };
        try { Process.Start(psi); }
        catch (Exception ex)
        {
            MessageBox.Show("Lancement impossible :\n" + ex.Message, "Tout Compte Fait", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        return 0;
    }
}