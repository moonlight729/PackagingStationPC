using System.Printing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using PackagingStationPC.Models;
using IOPath = System.IO.Path;

namespace PackagingStationPC.Services;

public interface ILabelPrinterService
{
    string Mode { get; }
    IReadOnlyList<string> GetAvailablePrinters();
    Task<IntegrationResult> TestAsync(CancellationToken cancellationToken = default);
    Task<IntegrationResult> PrintAsync(ProductionRecord record, CancellationToken cancellationToken = default);
}

public sealed class MockLabelPrinterService : ILabelPrinterService
{
    public string Mode => "mock";

    public IReadOnlyList<string> GetAvailablePrinters() => ["模拟打印机"];

    public async Task<IntegrationResult> TestAsync(CancellationToken cancellationToken = default)
    {
        await Task.Delay(250, cancellationToken);
        return new IntegrationResult(true, "模拟打印机状态正常。");
    }

    public async Task<IntegrationResult> PrintAsync(
        ProductionRecord record,
        CancellationToken cancellationToken = default)
    {
        await Task.Delay(350, cancellationToken);
        return new IntegrationResult(true, $"模拟标签打印成功: {record.PackageSn}");
    }
}

public interface IBarTenderSdkAdapter
{
    IntegrationResult Test(PrinterConfiguration configuration);
    IntegrationResult Print(PrinterConfiguration configuration, string packageSn);
}

public sealed class BarTenderLabelPrinterService : ILabelPrinterService
{
    private readonly PrinterConfiguration _configuration;
    private readonly IBarTenderSdkAdapter _adapter;

    public BarTenderLabelPrinterService(
        PrinterConfiguration configuration,
        IBarTenderSdkAdapter? adapter = null)
    {
        _configuration = configuration;
        _adapter = adapter ?? new BarTenderSdkAdapter();
    }

    public string Mode => "bartender";

    public IReadOnlyList<string> GetAvailablePrinters()
    {
        using var server = new LocalPrintServer();
        return server.GetPrintQueues()
            .Select(queue => queue.FullName)
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public async Task<IntegrationResult> TestAsync(CancellationToken cancellationToken = default)
    {
        var validation = ValidateConfiguration();
        if (validation is not null)
        {
            return validation;
        }

        try
        {
            return await Task.Run(() => _adapter.Test(_configuration), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new IntegrationResult(false, $"BarTender 检测失败: {ex.Message}");
        }
    }

    public async Task<IntegrationResult> PrintAsync(
        ProductionRecord record,
        CancellationToken cancellationToken = default)
    {
        var validation = ValidateConfiguration();
        if (validation is not null)
        {
            return validation;
        }

        try
        {
            return await Task.Run(
                () => _adapter.Print(_configuration, record.PackageSn),
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new IntegrationResult(false, $"BarTender 打印失败: {ex.Message}");
        }
    }

    private IntegrationResult? ValidateConfiguration()
    {
        if (string.IsNullOrWhiteSpace(_configuration.TemplatePath))
        {
            return new IntegrationResult(false, "BarTender 配置缺少 templatePath。");
        }

        if (!File.Exists(_configuration.TemplatePath))
        {
            return new IntegrationResult(false, $"BarTender 模板不存在: {_configuration.TemplatePath}");
        }

        if (string.IsNullOrWhiteSpace(_configuration.SnDataSourceName))
        {
            return new IntegrationResult(false, "BarTender 配置缺少 snDataSourceName。");
        }

        if (_configuration.Copies is < 1 or > 100)
        {
            return new IntegrationResult(false, "BarTender 打印份数必须在 1 到 100 之间。");
        }

        return null;
    }
}

public sealed class BarTenderSdkAdapter : IBarTenderSdkAdapter
{
    public IntegrationResult Test(PrinterConfiguration configuration)
    {
        try
        {
            var assembly = LoadSdkAssembly(configuration.BarTenderSdkAssemblyPath);
            return TestWithSdk(assembly, configuration);
        }
        catch (BadImageFormatException)
        {
            return TestWithAutomation(configuration);
        }
    }

    private static IntegrationResult TestWithSdk(Assembly assembly, PrinterConfiguration configuration)
    {
        return WithFormat(
            assembly,
            configuration,
            format =>
            {
                dynamic namedDataSource = format.SubStrings[configuration.SnDataSourceName];
                if (namedDataSource is null)
                {
                    throw new InvalidOperationException(
                        $"模板中不存在命名数据源 {configuration.SnDataSourceName}。");
                }

                _ = Convert.ToString(namedDataSource.Value);
                return new IntegrationResult(
                    true,
                    $"BarTender SDK 模板可用: {IOPath.GetFileName(configuration.TemplatePath)}");
            });
    }

    public IntegrationResult Print(PrinterConfiguration configuration, string packageSn)
    {
        try
        {
            var assembly = LoadSdkAssembly(configuration.BarTenderSdkAssemblyPath);
            return PrintWithSdk(assembly, configuration, packageSn);
        }
        catch (BadImageFormatException)
        {
            return PrintWithAutomation(configuration, packageSn);
        }
    }

    private static IntegrationResult PrintWithSdk(
        Assembly assembly,
        PrinterConfiguration configuration,
        string packageSn)
    {
        return WithFormat(
            assembly,
            configuration,
            format =>
            {
                dynamic namedDataSource = format.SubStrings[configuration.SnDataSourceName];
                if (namedDataSource is null)
                {
                    throw new InvalidOperationException(
                        $"模板中不存在命名数据源 {configuration.SnDataSourceName}。");
                }

                namedDataSource.Value = packageSn;
                if (!string.IsNullOrWhiteSpace(configuration.PrinterName))
                {
                    format.PrintSetup.PrinterName = configuration.PrinterName;
                }
                format.PrintSetup.IdenticalCopiesOfLabel = configuration.Copies;

                var timeoutMilliseconds = checked(Math.Clamp(
                    configuration.PrintTimeoutSeconds,
                    1,
                    300) * 1000);
                var result = format.Print($"PackagingStationPC-{packageSn}", timeoutMilliseconds);
                var resultName = Convert.ToString(result) ?? "Unknown";
                return string.Equals(resultName, "Success", StringComparison.OrdinalIgnoreCase)
                    ? new IntegrationResult(true, $"BarTender 标签打印成功: {packageSn}")
                    : new IntegrationResult(false, $"BarTender 打印失败: {resultName}");
            });
    }

    private static IntegrationResult TestWithAutomation(PrinterConfiguration configuration)
    {
        return WithAutomationFormat(
            configuration,
            format =>
            {
                _ = Convert.ToString(format.GetNamedSubStringValue(configuration.SnDataSourceName));
                return new IntegrationResult(
                    true,
                    $"BarTender 2016 Automation 模板可用: {IOPath.GetFileName(configuration.TemplatePath)}");
            });
    }

    private static IntegrationResult PrintWithAutomation(
        PrinterConfiguration configuration,
        string packageSn)
    {
        return WithAutomationFormat(
            configuration,
            format =>
            {
                format.SetNamedSubStringValue(configuration.SnDataSourceName, packageSn);
                format.IdenticalCopiesOfLabel = configuration.Copies;
                var result = format.PrintOut(false, false);
                var resultCode = Convert.ToInt32(result);
                return resultCode == 0
                    ? new IntegrationResult(true, $"BarTender 标签打印成功: {packageSn}")
                    : new IntegrationResult(false, $"BarTender 打印失败，返回码: {resultCode}");
            });
    }

    private static IntegrationResult WithAutomationFormat(
        PrinterConfiguration configuration,
        Func<dynamic, IntegrationResult> operation)
    {
        var applicationType = Type.GetTypeFromProgID("BarTender.Application", throwOnError: true)
            ?? throw new InvalidOperationException("未注册 BarTender.Application 自动化接口。");
        dynamic application = Activator.CreateInstance(applicationType)
            ?? throw new InvalidOperationException("无法启动 BarTender Automation。");
        dynamic? format = null;
        try
        {
            application.Visible = false;
            var printerName = configuration.PrinterName ?? string.Empty;
            format = application.Formats.Open(
                IOPath.GetFullPath(configuration.TemplatePath),
                false,
                printerName);
            return operation(format);
        }
        finally
        {
            if (format is not null)
            {
                try
                {
                    format.Close(1); // btDoNotSaveChanges
                }
                catch
                {
                }
                ReleaseComObject(format);
            }

            try
            {
                application.Quit(1); // btDoNotSaveChanges
            }
            catch
            {
            }
            ReleaseComObject(application);
        }
    }

    private static void ReleaseComObject(object value)
    {
        if (Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }

    private static IntegrationResult WithFormat(
        Assembly assembly,
        PrinterConfiguration configuration,
        Func<dynamic, IntegrationResult> operation)
    {
        var engineType = assembly.GetType("Seagull.BarTender.Print.Engine", throwOnError: true)!;
        var saveOptionsType = assembly.GetType("Seagull.BarTender.Print.SaveOptions", throwOnError: true)!;
        var doNotSave = Enum.Parse(saveOptionsType, "DoNotSaveChanges");
        dynamic engine = Activator.CreateInstance(engineType)
            ?? throw new InvalidOperationException("无法创建 BarTender Engine。");
        dynamic? format = null;
        var started = false;
        try
        {
            engine.Start();
            started = true;
            format = engine.Documents.Open(IOPath.GetFullPath(configuration.TemplatePath));
            return operation(format);
        }
        finally
        {
            if (format is not null)
            {
                try
                {
                    format.Close(doNotSave);
                }
                catch
                {
                }
                if (format is IDisposable disposableFormat)
                {
                    disposableFormat.Dispose();
                }
            }

            if (started)
            {
                try
                {
                    engine.Stop(doNotSave);
                }
                catch
                {
                }
            }
            if (engine is IDisposable disposableEngine)
            {
                disposableEngine.Dispose();
            }
        }
    }

    private static Assembly LoadSdkAssembly(string configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var fullPath = IOPath.GetFullPath(Environment.ExpandEnvironmentVariables(configuredPath));
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException("配置的 BarTender SDK DLL 不存在。", fullPath);
            }
            return Assembly.LoadFrom(fullPath);
        }

        try
        {
            return Assembly.Load("Seagull.BarTender.Print");
        }
        catch (FileNotFoundException)
        {
        }

        var localPath = IOPath.Combine(AppContext.BaseDirectory, "Seagull.BarTender.Print.dll");
        if (File.Exists(localPath))
        {
            return Assembly.LoadFrom(localPath);
        }

        foreach (var root in BarTenderInstallRoots())
        {
            try
            {
                var path = Directory.EnumerateFiles(
                        root,
                        "Seagull.BarTender.Print.dll",
                        SearchOption.AllDirectories)
                    .FirstOrDefault();
                if (path is not null)
                {
                    return Assembly.LoadFrom(path);
                }
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        throw new FileNotFoundException(
            "未找到 Seagull.BarTender.Print.dll。请安装 BarTender .NET SDK，或配置 barTenderSdkAssemblyPath。");
    }

    private static IEnumerable<string> BarTenderInstallRoots()
    {
        foreach (var programFiles in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
                 })
        {
            var root = IOPath.Combine(programFiles, "Seagull");
            if (Directory.Exists(root))
            {
                yield return root;
            }
        }
    }
}

public sealed class WindowsLabelPrinterService : ILabelPrinterService
{
    private readonly PrinterConfiguration _configuration;

    public WindowsLabelPrinterService(PrinterConfiguration configuration)
    {
        _configuration = configuration;
    }

    public string Mode => "windows";

    public IReadOnlyList<string> GetAvailablePrinters()
    {
        using var server = new LocalPrintServer();
        return server.GetPrintQueues()
            .Select(queue => queue.FullName)
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public Task<IntegrationResult> TestAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var queue = ResolveQueue();
            queue.Refresh();
            if (queue.IsOffline || queue.IsNotAvailable || queue.IsInError)
            {
                return Task.FromResult(new IntegrationResult(false, $"打印机不可用: {queue.FullName}"));
            }

            return Task.FromResult(new IntegrationResult(true, $"打印机就绪: {queue.FullName}"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new IntegrationResult(false, $"打印机测试失败: {ex.Message}"));
        }
    }

    public Task<IntegrationResult> PrintAsync(
        ProductionRecord record,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var queue = ResolveQueue();
            var visual = LabelVisualFactory.Create(
                record.PackageSn,
                record.ProductName,
                record.DeviceSerial,
                _configuration.LabelWidthMm,
                _configuration.LabelHeightMm);
            var printDialog = new PrintDialog { PrintQueue = queue };
            printDialog.PrintVisual(visual, $"SN {record.PackageSn}");
            return Task.FromResult(new IntegrationResult(true, $"标签已发送到打印机: {queue.FullName}"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new IntegrationResult(false, $"标签打印失败: {ex.Message}"));
        }
    }

    private PrintQueue ResolveQueue()
    {
        var server = new LocalPrintServer();
        if (string.IsNullOrWhiteSpace(_configuration.PrinterName))
        {
            return LocalPrintServer.GetDefaultPrintQueue();
        }

        var queue = server.GetPrintQueues().FirstOrDefault(item =>
            string.Equals(item.FullName, _configuration.PrinterName, StringComparison.OrdinalIgnoreCase));
        if (queue is null)
        {
            server.Dispose();
            throw new InvalidOperationException($"未找到打印机 {_configuration.PrinterName}。");
        }

        server.Dispose();
        return queue;
    }
}

public static class LabelPrinterServiceFactory
{
    public static ILabelPrinterService Create(PrinterConfiguration configuration) =>
        configuration.Mode.Trim().ToLowerInvariant() switch
        {
            "bartender" => new BarTenderLabelPrinterService(configuration),
            "windows" => new WindowsLabelPrinterService(configuration),
            _ => new MockLabelPrinterService()
        };
}

public static class LabelVisualFactory
{
    public static FrameworkElement Create(
        string sn,
        string productName,
        string deviceSerial,
        double widthMm,
        double heightMm)
    {
        var width = MillimetersToDeviceUnits(widthMm);
        var height = MillimetersToDeviceUnits(heightMm);
        var canvas = new Canvas
        {
            Width = width,
            Height = height,
            Background = Brushes.White
        };

        AddText(canvas, productName, 10, 6, 14, FontWeights.SemiBold);
        AddBarcode(canvas, sn, 10, 28, width - 20, Math.Max(40, height - 68));
        AddText(canvas, sn, 10, height - 34, 15, FontWeights.Bold);
        AddText(canvas, $"DUT: {deviceSerial}", width * 0.52, height - 30, 8, FontWeights.Normal);
        canvas.Measure(new Size(width, height));
        canvas.Arrange(new Rect(0, 0, width, height));
        canvas.UpdateLayout();
        return canvas;
    }

    private static void AddBarcode(Canvas canvas, string value, double left, double top, double width, double height)
    {
        var modules = Code128Encoder.GetModuleWidths(value);
        const int quietZoneModules = 10;
        var moduleWidth = width / (modules.Sum() + quietZoneModules * 2);
        var x = left + quietZoneModules * moduleWidth;
        var isBar = true;
        foreach (var moduleCount in modules)
        {
            var elementWidth = moduleCount * moduleWidth;
            if (isBar)
            {
                var bar = new Rectangle
                {
                    Width = elementWidth,
                    Height = height,
                    Fill = Brushes.Black,
                    SnapsToDevicePixels = true
                };
                Canvas.SetLeft(bar, x);
                Canvas.SetTop(bar, top);
                canvas.Children.Add(bar);
            }

            x += elementWidth;
            isBar = !isBar;
        }
    }

    private static void AddText(
        Canvas canvas,
        string value,
        double left,
        double top,
        double fontSize,
        FontWeight weight)
    {
        var text = new TextBlock
        {
            Text = value,
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            FontSize = fontSize,
            FontWeight = weight,
            Foreground = Brushes.Black
        };
        Canvas.SetLeft(text, left);
        Canvas.SetTop(text, top);
        canvas.Children.Add(text);
    }

    private static double MillimetersToDeviceUnits(double value) => value * 96d / 25.4d;
}
