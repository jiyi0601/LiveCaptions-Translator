using System.Windows;
using LiveCaptionsTranslator.utils;
namespace LiveCaptionsTranslator;

public partial class App : Application
{
    public App()
    {
        RuntimeOptions.Initialize(Environment.GetCommandLineArgs());
        Translator.Setting.Save();
        Translator.Start();
    }
    protected override void OnExit(ExitEventArgs e)
    {
        Translator.StopAsync().GetAwaiter().GetResult();
        Translator.Setting.Save();
        if (Translator.Window != null)
        {
            try
            {
                LiveCaptionsHandler.RestoreLiveCaptions(Translator.Window);
                LiveCaptionsHandler.KillLiveCaptions(Translator.Window);
            }
            catch { /* Captions may already have been closed by the user. */ }
        }
        base.OnExit(e);
    }
}
