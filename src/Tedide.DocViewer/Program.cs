using Tedide.DocViewer;
using Tedide.Theming;
using Terminal.Gui.App;

using var database = new DocDatabase(Path.Combine(AppContext.BaseDirectory, "Docs.db"));

Application.Init();
try
{
    ThemeSwitcher.Apply(ThemeSettings.Load().Theme);
    Application.Run(new DocViewerShell(database));
}
finally
{
    Application.Shutdown();
}
