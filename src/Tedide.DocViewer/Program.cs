using Tedide.DocViewer;
using Tedide.Theming;
using Terminal.Gui.App;

Application.Init();
try
{
    ThemeSwitcher.Apply(ThemeSettings.Load().Theme);
    Application.Run(new DocViewerShell());
}
finally
{
    Application.Shutdown();
}
