using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Windows.Storage;

namespace XpdfReader.WinUI;

public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();

        try
        {
            AppActivationArguments? activatedArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
            if (activatedArgs is not null &&
                activatedArgs.Kind == ExtendedActivationKind.File &&
                activatedArgs.Data is Windows.ApplicationModel.Activation.IFileActivatedEventArgs fileArgs)
            {
                StorageFile? activatedFile = fileArgs.Files.OfType<StorageFile>().FirstOrDefault();
                if (activatedFile is not null)
                {
                    _window.QueueInitialDocument(activatedFile.Path);
                    return;
                }
            }
        }
        catch (Exception)
        {
            // Unpackaged installs receive the document path on the command line.
        }

        string[] arguments = System.Environment.GetCommandLineArgs();
        if (arguments.Length > 1)
        {
            _window.QueueInitialDocument(arguments[1]);
        }
    }
}
