using System.Windows;
using PackagingStationPC.Services;
using PackagingStationPC.ViewModels;

namespace PackagingStationPC;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var configuration = ConfigurationService.Load();
            var dataDirectory = ConfigurationService.GetDataDirectory();
            var snGenerator = new SnGenerator();
            var repository = new SqliteProductionRepository(
                Path.Combine(dataDirectory, configuration.DatabaseFileName),
                snGenerator);
            var processRunner = new ProcessRunner();
            var adbClient = new AdbClient(configuration.Adb, processRunner);
            var bandSerialVersionService = new BandSerialVersionService();
            var glassesAcmService = new GlassesAcmService(adbClient);
            var versionService = new DeviceVersionService(adbClient, bandSerialVersionService, glassesAcmService);
            var logger = new AppLogger(Path.Combine(dataDirectory, "packaging-station.log"));
            var uploadService = FactoryUploadServiceFactory.Create(configuration.FactorySystem, logger);
            var scannerService = new ScannerService();
            var viewModel = new MainViewModel(
                configuration,
                versionService,
                snGenerator,
                repository,
                uploadService,
                scannerService,
                logger);
            var window = new MainWindow(viewModel);
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                ex.ToString(),
                "PackagingStationPC 启动失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
        }
    }
}
