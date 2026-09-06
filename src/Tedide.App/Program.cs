using Terminal.Gui.App;
using Tedide.App;
using Tedide.App.Theming;

Application.Init();
try
{
    ThemeSwitcher.Apply(ThemeSwitcher.Current);
    var shell = new AppShell();
    Application.Run(shell);
}
finally
{
    Application.Shutdown();
}
