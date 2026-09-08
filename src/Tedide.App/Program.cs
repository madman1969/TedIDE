using Terminal.Gui.App;
using Tedide.App;
using Tedide.App.Highlighting;
using Tedide.Theming;

Application.Init();
try
{
    Cc65AssemblyHighlighting.Register();
    Cc65ListingHighlighting.Register();
    Cc65LinkerMapHighlighting.Register();
    Cc65LabelsHighlighting.Register();
    ThemeSwitcher.Apply(ThemeSettings.Load().Theme);
    var shell = new AppShell();
    Application.Run(shell);
    shell.SaveLayoutSettings();
}
finally
{
    Application.Shutdown();
}
