using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Castor.AI.Dashboard.ViewModels;
using Castor.AI.Dashboard.Views;

namespace Castor.AI.Dashboard;

public sealed class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewModel = new MainViewModel();
            desktop.MainWindow = new MainWindow { DataContext = viewModel };
            desktop.Exit += (_, _) => viewModel.Shutdown();

            // --autostart [profile name]: start a session right away (demos, screenshots).
            var arguments = desktop.Args ?? [];
            var index = Array.IndexOf(arguments, "--autostart");
            if (index >= 0)
            {
                var name = index + 1 < arguments.Length ? arguments[index + 1] : null;
                viewModel.SelectedProfile = viewModel.Profiles.FirstOrDefault(p => p.Name == name) ?? viewModel.SelectedProfile;
                desktop.MainWindow.Opened += (_, _) => viewModel.StartCommand.Execute(null);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
