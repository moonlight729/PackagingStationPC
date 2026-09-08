using System.Collections.ObjectModel;
using PackagingStationPC.Models;
using PackagingStationPC.Services;

namespace PackagingStationPC.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly AppConfiguration _configuration;
    private readonly DeviceVersionService _versionService;
    private readonly SnGenerator _snGenerator;
    private readonly IProductionRepository _repository;
    private readonly IFactoryUploadService _uploadService;
    private readonly ScannerService _scannerService;
    private readonly AppLogger _logger;
    private AdbDeviceInfo? _selectedDevice;
    private DeviceVersionSnapshot? _snapshot;
    private ProductionRecord? _currentRecord;
    private string _selectedFactoryCode;
    private int _selectedYear;
    private int _selectedWeek;
    private int _nextSequence;
    private bool _isBusy;
    private string _statusMessage = "正在初始化...";
    private string _moduleMessage = string.Empty;
    private bool _moduleHasError;
    private string _scannerInput = string.Empty;
    private string _scannedPackageCode = string.Empty;
    private string _loginStageStatus = "等待上传";
    private string _checkRouteStageStatus = "等待上传";
    private string _createRouteStageStatus = "等待上传";

    public MainViewModel(
        AppConfiguration configuration,
        DeviceVersionService versionService,
        SnGenerator snGenerator,
        IProductionRepository repository,
        IFactoryUploadService uploadService,
        ScannerService scannerService,
        AppLogger logger)
    {
        _configuration = configuration;
        _versionService = versionService;
        _snGenerator = snGenerator;
        _repository = repository;
        _uploadService = uploadService;
        _scannerService = scannerService;
        _logger = logger;

        var current = SnGenerator.CurrentIsoYearWeek(DateTime.Today);
        _selectedYear = current.Year;
        _selectedWeek = current.Week;
        _nextSequence = Math.Clamp(configuration.Sn.InitialSequence, 1, SnGenerator.MaximumSequence);
        _selectedFactoryCode = configuration.Sn.FactoryCodes.Contains(
            configuration.Sn.DefaultFactoryCode,
            StringComparer.OrdinalIgnoreCase)
            ? configuration.Sn.DefaultFactoryCode
            : configuration.Sn.FactoryCodes.FirstOrDefault() ?? "F";

        FactoryCodes = configuration.Sn.FactoryCodes.Length > 0 ? configuration.Sn.FactoryCodes : ["F"];
        Years = Enumerable.Range(Math.Max(0, current.Year - 2), 8).Where(year => year <= 99).ToArray();
        Weeks = Enumerable.Range(1, 53).ToArray();

        RefreshDevicesCommand = new AsyncRelayCommand(() => RunBusyAsync(RefreshDevicesAsync), () => !IsBusy);
        QueryVersionsCommand = new AsyncRelayCommand(() => RunBusyAsync(QueryVersionsAsync), () => !IsBusy);
        RefreshSequenceCommand = new AsyncRelayCommand(() => RunBusyAsync(LoadNextSequenceAsync), () => !IsBusy && CurrentRecord is null);
        SetSequenceCommand = new AsyncRelayCommand(() => RunBusyAsync(SetSequenceAsync), () => !IsBusy && CurrentRecord is null);
        ReserveSnCommand = new AsyncRelayCommand(() => RunBusyAsync(ReserveSnAsync), CanReserveSn);
        UploadCommand = new AsyncRelayCommand(() => RunBusyAsync(UploadAsync), () => !IsBusy && CurrentRecord is not null);
        ScanCommand = new AsyncRelayCommand(() => RunBusyAsync(ProcessScanAsync),
            () => !IsBusy && !string.IsNullOrWhiteSpace(ScannerInput));
        NextDeviceCommand = new AsyncRelayCommand(() => RunBusyAsync(NextDeviceAsync), CanMoveNext);
        RefreshRecordsCommand = new AsyncRelayCommand(() => RunBusyAsync(RefreshRecordsAsync), () => !IsBusy);

        _versionService.ProgressChanged += message => StatusMessage = message;
        _uploadService.ProgressChanged += OnUploadProgressChanged;
    }

    public string ProductCode => _configuration.Sn.ProductCode;
    public string ProductName => _configuration.Sn.ProductName;
    public string StationCode => _configuration.StationCode;
    public string UploadMode => _uploadService.Mode;
    public string UploadEndpoint =>
        _configuration.FactorySystem.BaseUrl.TrimEnd('/') + "/" +
        _configuration.FactorySystem.CreateRoutePath.TrimStart('/');

    public string ScannerInput
    {
        get => _scannerInput;
        set
        {
            if (SetProperty(ref _scannerInput, value))
            {
                ScanCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string ScannedPackageCode
    {
        get => _scannedPackageCode;
        private set => SetProperty(ref _scannedPackageCode, value);
    }

    public IReadOnlyList<string> FactoryCodes { get; }
    public IReadOnlyList<int> Years { get; }
    public IReadOnlyList<int> Weeks { get; }

    public ObservableCollection<AdbDeviceInfo> Devices { get; } = [];
    public ObservableCollection<VersionRowViewModel> VersionRows { get; } = [];
    public ObservableCollection<ProductionRecord> RecentRecords { get; } = [];

    public AdbDeviceInfo? SelectedDevice
    {
        get => _selectedDevice;
        set => SetProperty(ref _selectedDevice, value);
    }

    public string SelectedFactoryCode
    {
        get => _selectedFactoryCode;
        set
        {
            if (SetProperty(ref _selectedFactoryCode, value))
            {
                SelectionChanged();
            }
        }
    }

    public int SelectedYear
    {
        get => _selectedYear;
        set
        {
            if (SetProperty(ref _selectedYear, value))
            {
                SelectionChanged();
            }
        }
    }

    public int SelectedWeek
    {
        get => _selectedWeek;
        set
        {
            if (SetProperty(ref _selectedWeek, value))
            {
                SelectionChanged();
            }
        }
    }

    public int NextSequence
    {
        get => _nextSequence;
        set
        {
            if (SetProperty(ref _nextSequence, value))
            {
                OnPropertyChanged(nameof(SnPreview));
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string ModuleMessage
    {
        get => _moduleMessage;
        private set => SetProperty(ref _moduleMessage, value);
    }

    public string ModuleMessageBrush => ModuleHasError ? "#B42318" : "#172023";

    private bool ModuleHasError
    {
        get => _moduleHasError;
        set
        {
            if (SetProperty(ref _moduleHasError, value))
            {
                OnPropertyChanged(nameof(ModuleMessageBrush));
            }
        }
    }

    public string SnPreview
    {
        get
        {
            if (CurrentRecord is not null)
            {
                return CurrentRecord.PackageSn;
            }

            try
            {
                return _snGenerator.Generate(CurrentParts());
            }
            catch
            {
                return "SN 参数无效";
            }
        }
    }

    public ProductionRecord? CurrentRecord
    {
        get => _currentRecord;
        private set
        {
            if (SetProperty(ref _currentRecord, value))
            {
                OnPropertyChanged(nameof(SnPreview));
                OnPropertyChanged(nameof(CurrentUploadStatus));
                OnPropertyChanged(nameof(CurrentDeviceSerial));
                RaiseCommandStates();
            }
        }
    }

    public string CurrentUploadStatus => CurrentRecord?.UploadStatus.ToString() ?? "未预留";
    public string CurrentDeviceSerial => CurrentRecord?.DeviceSerial ?? _snapshot?.DeviceSerial ?? "-";
    public string LoginStageStatus
    {
        get => _loginStageStatus;
        private set => SetProperty(ref _loginStageStatus, value);
    }

    public string CheckRouteStageStatus
    {
        get => _checkRouteStageStatus;
        private set => SetProperty(ref _checkRouteStageStatus, value);
    }

    public string CreateRouteStageStatus
    {
        get => _createRouteStageStatus;
        private set => SetProperty(ref _createRouteStageStatus, value);
    }

    public AsyncRelayCommand RefreshDevicesCommand { get; }
    public AsyncRelayCommand QueryVersionsCommand { get; }
    public AsyncRelayCommand RefreshSequenceCommand { get; }
    public AsyncRelayCommand SetSequenceCommand { get; }
    public AsyncRelayCommand ReserveSnCommand { get; }
    public AsyncRelayCommand UploadCommand { get; }
    public AsyncRelayCommand ScanCommand { get; }
    public AsyncRelayCommand NextDeviceCommand { get; }
    public AsyncRelayCommand RefreshRecordsCommand { get; }

    public async Task InitializeAsync()
    {
        await RunBusyAsync(async () =>
        {
            await _repository.InitializeAsync();
            await LoadNextSequenceAsync();
            await RefreshRecordsAsync();
            await RefreshDevicesAsync();
            StatusMessage = Devices.Count == 0 ? "工位已就绪，未检测到 ADB 设备。" : "工位已就绪。";
            ModuleMessage = "请扫描外包装盒码，程序将自动读取设备信息并上传。";
        });
    }

    private async Task ProcessScanAsync()
    {
        var packageCode = _scannerService.Normalize(ScannerInput);
        ScannerInput = string.Empty;
        if (string.IsNullOrWhiteSpace(packageCode) || packageCode.Length > 128 || packageCode.Any(char.IsControl))
        {
            throw new InvalidOperationException("外包装盒码为空、过长或包含控制字符，请重新扫描。");
        }

        ResetCurrentUnit();
        ScannedPackageCode = packageCode;
        ModuleHasError = false;
        await _logger.WriteAsync("INFO", $"Scan received package={packageCode}, selectedDevice={SelectedDevice?.Serial ?? "auto"}");
        ModuleMessage = $"已扫描 {packageCode}，正在读取设备 SN 和版本。";
        await QueryVersionsAsync();
        if (_snapshot is null)
        {
            throw new InvalidOperationException("设备信息读取没有返回结果。");
        }

        var validationError = ValidateForUpload(_snapshot);
        if (validationError is not null)
        {
            throw new InvalidOperationException($"设备信息不完整，禁止上传 MES：{validationError}");
        }

        var reservation = await _repository.BindScannedPackageAsync(
            packageCode,
            _snapshot,
            _configuration.Sn.ProductCode,
            _configuration.Sn.ProductName,
            _configuration.StationCode);
        CurrentRecord = reservation.Record;
        await _logger.WriteAsync(
            "INFO",
            $"Package binding saved package={packageCode}, device={_snapshot.DeviceSerial}, adb={_snapshot.AdbSerial}, existing={reservation.WasExisting}");
        await RefreshRecordsAsync();
        await UploadAsync();
    }

    private void ResetCurrentUnit()
    {
        CurrentRecord = null;
        _snapshot = null;
        ScannedPackageCode = string.Empty;
        VersionRows.Clear();
        ModuleMessage = string.Empty;
        ModuleHasError = false;
        LoginStageStatus = "等待上传";
        CheckRouteStageStatus = "等待上传";
        CreateRouteStageStatus = "等待上传";
    }

    private void OnUploadProgressChanged(FactoryUploadProgress progress)
    {
        var state = progress.State switch
        {
            "Running" => "执行中",
            "Succeeded" => "成功",
            "Failed" => "失败",
            "Skipped" => "未执行",
            _ => progress.State
        };
        var display = string.IsNullOrWhiteSpace(progress.Message)
            ? state
            : $"{state} | {progress.Message}";
        switch (progress.Stage)
        {
            case "clientLogin":
                LoginStageStatus = display;
                break;
            case "checkRoute":
                CheckRouteStageStatus = display;
                break;
            case "createRoute":
                CreateRouteStageStatus = display;
                break;
        }
    }

    private static string? ValidateForUpload(DeviceVersionSnapshot snapshot)
    {
        var missing = new List<string>();
        var box = snapshot.Components.FirstOrDefault(item => item.Component == "box");
        Require(box, "盒子", "sn", "uboot", "kernel", "rootfs");
        var glasses = snapshot.Components.FirstOrDefault(item => item.Component == "glasses");
        Require(glasses, "眼镜", "sn", "uboot", "kernel", "rootfs", "app");
        var band = snapshot.Components.FirstOrDefault(item => item.Component == "band");
        Require(band, "手环", "leftSn", "rightSn");
        return missing.Count == 0 ? null : string.Join("、", missing);

        void Require(VersionComponentResult? component, string displayName, params string[] fields)
        {
            foreach (var field in fields)
            {
                if (component is null || !component.Values.TryGetValue(field, out var value) ||
                    string.IsNullOrWhiteSpace(value))
                {
                    missing.Add($"{displayName}.{DisplayField(field)}");
                }
            }
        }
    }

    private async Task RefreshDevicesAsync()
    {
        var selectedSerial = SelectedDevice?.Serial;
        var devices = await _versionService.GetDevicesAsync();
        Devices.Clear();
        foreach (var device in devices)
        {
            Devices.Add(device);
        }

        SelectedDevice = Devices.FirstOrDefault(device =>
                             string.Equals(device.Serial, selectedSerial, StringComparison.OrdinalIgnoreCase))
                         ?? Devices.FirstOrDefault(device => device.IsOnline);
        StatusMessage = Devices.Count == 0 ? "未检测到 ADB 设备。" : $"检测到 {Devices.Count} 台 ADB 设备。";
    }

    private async Task QueryVersionsAsync()
    {
        _snapshot = await _versionService.CollectAsync(SelectedDevice?.Serial);
        VersionRows.Clear();
        foreach (var component in _snapshot.Components)
        {
            if (component.Values.Count == 0)
            {
                VersionRows.Add(new VersionRowViewModel(
                    component.DisplayName,
                    "-",
                    component.Message,
                    StateText(component.State),
                    component.Source));
                continue;
            }

            foreach (var value in component.Values)
            {
                VersionRows.Add(new VersionRowViewModel(
                    DisplayComponent(component, value.Key),
                    DisplayField(value.Key),
                    string.IsNullOrWhiteSpace(value.Value) ? "未获取" : value.Value,
                    StateText(component.State),
                    component.Source));
            }
        }

        OnPropertyChanged(nameof(CurrentDeviceSerial));
        var validationError = ValidateForUpload(_snapshot);
        ModuleHasError = validationError is not null;
        ModuleMessage = validationError is null
            ? "产品 SN 和必需版本已完整读取。"
            : $"设备信息读取不完整：{validationError}";
        foreach (var component in _snapshot.Components)
        {
            var values = component.Values.Count == 0
                ? "none"
                : string.Join(",", component.Values.Select(item => $"{item.Key}={item.Value}"));
            await _logger.WriteAsync(
                component.State is QueryState.Success ? "INFO" : "ERROR",
                $"Version component={component.Component}, state={component.State}, values={values}, source={component.Source}, message={component.Message}");
        }
        await _logger.WriteAsync("INFO", $"Version query device={_snapshot.AdbSerial}, complete={_snapshot.IsComplete}");
        RaiseCommandStates();
    }

    private static string DisplayComponent(VersionComponentResult component, string field) => field switch
    {
        "leftSn" or "leftVersion" => "左手环",
        "rightSn" or "rightVersion" => "右手环",
        _ => component.DisplayName
    };

    private static string DisplayField(string field) => field switch
    {
        "sn" or "leftSn" or "rightSn" => "SN",
        "leftVersion" or "rightVersion" => "版本",
        "uboot" => "U-Boot 版本",
        "kernel" => "Kernel 版本",
        "rootfs" => "RootFS 版本",
        "app" => "应用版本",
        _ => field
    };

    private async Task LoadNextSequenceAsync()
    {
        var parts = CurrentParts();
        NextSequence = await _repository.GetNextSequenceAsync(parts, _configuration.Sn.InitialSequence);
        StatusMessage = $"当前号段下一流水号: {NextSequence:000000}";
    }

    private async Task SetSequenceAsync()
    {
        _snGenerator.Validate(CurrentParts());
        await _repository.SetNextSequenceAsync(CurrentParts(), NextSequence, "operator manual update");
        StatusMessage = $"下一流水号已设置为 {NextSequence:000000}，操作已记录。";
        await _logger.WriteAsync("WARN", $"Sequence changed factory={SelectedFactoryCode}, year={SelectedYear:00}, week={SelectedWeek:00}, next={NextSequence:000000}");
    }

    private async Task ReserveSnAsync()
    {
        if (_snapshot is null)
        {
            throw new InvalidOperationException("请先读取设备版本。");
        }

        if (_configuration.Adb.RequireCompleteVersionSet && !_snapshot.IsComplete)
        {
            throw new InvalidOperationException("必需版本项不完整，不能预留 SN。");
        }

        var reservation = await _repository.ReserveAsync(
            _snapshot,
            _configuration.Sn.ProductName,
            _configuration.StationCode,
            CurrentParts());
        CurrentRecord = reservation.Record;
        ModuleMessage = reservation.WasExisting
            ? $"设备已有包装记录，已恢复原 SN {reservation.Record.PackageSn}。"
            : $"SN {reservation.Record.PackageSn} 已预留。";
        await LoadNextSequenceAsync();
        await RefreshRecordsAsync();
        await _logger.WriteAsync("INFO", $"SN reserved sn={reservation.Record.PackageSn}, device={reservation.Record.DeviceSerial}, existing={reservation.WasExisting}");
    }

    private async Task UploadAsync()
    {
        var record = CurrentRecord ?? throw new InvalidOperationException("请先预留 SN。");
        var result = await _uploadService.UploadAsync(record);
        await _repository.UpdateUploadAsync(record.Id, result);
        CurrentRecord = await _repository.GetByIdAsync(record.Id);
        ModuleMessage = result.Message;
        ModuleHasError = !result.Success;
        await RefreshRecordsAsync();
        await _logger.WriteAsync(result.Success ? "INFO" : "ERROR", $"Upload sn={record.PackageSn}: {result.Message}");
    }

    private async Task NextDeviceAsync()
    {
        var record = CurrentRecord ?? throw new InvalidOperationException("当前没有已预留的生产记录。");
        if (_configuration.Workflow.RequireUploadBeforeNext && record.UploadStatus != IntegrationStatus.Succeeded)
        {
            throw new InvalidOperationException("当前配置要求上传成功后才能进入下一台。");
        }

        CurrentRecord = null;
        _snapshot = null;
        ScannedPackageCode = string.Empty;
        VersionRows.Clear();
        ModuleMessage = "已进入下一台，请扫描新的外包装盒码。";
        await RefreshDevicesAsync();
    }

    private async Task RefreshRecordsAsync()
    {
        var records = await _repository.GetRecentAsync(200);
        RecentRecords.Clear();
        foreach (var record in records)
        {
            RecentRecords.Add(record);
        }
    }

    private bool CanReserveSn() =>
        !IsBusy && CurrentRecord is null && _snapshot is not null &&
        (!_configuration.Adb.RequireCompleteVersionSet || _snapshot.IsComplete);

    private bool CanMoveNext() => !IsBusy && CurrentRecord is not null;

    private SnParts CurrentParts() => new(
        _configuration.Sn.ProductCode,
        SelectedFactoryCode,
        SelectedYear,
        SelectedWeek,
        NextSequence);

    private void SelectionChanged()
    {
        OnPropertyChanged(nameof(SnPreview));
        if (CurrentRecord is null && !IsBusy)
        {
            _ = ReloadSequenceAfterSelectionAsync();
        }
    }

    private async Task ReloadSequenceAfterSelectionAsync()
    {
        try
        {
            await LoadNextSequenceAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    private async Task RunBusyAsync(Func<Task> action)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            StatusMessage = "操作失败。";
            ModuleMessage = ex.Message;
            ModuleHasError = true;
            await _logger.WriteAsync("ERROR", ex.ToString());
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RaiseCommandStates()
    {
        RefreshDevicesCommand.RaiseCanExecuteChanged();
        QueryVersionsCommand.RaiseCanExecuteChanged();
        RefreshSequenceCommand.RaiseCanExecuteChanged();
        SetSequenceCommand.RaiseCanExecuteChanged();
        ReserveSnCommand.RaiseCanExecuteChanged();
        UploadCommand.RaiseCanExecuteChanged();
        ScanCommand.RaiseCanExecuteChanged();
        NextDeviceCommand.RaiseCanExecuteChanged();
        RefreshRecordsCommand.RaiseCanExecuteChanged();
    }

    private static string StateText(QueryState state) => state switch
    {
        QueryState.Success => "成功",
        QueryState.Warning => "警告",
        QueryState.Failed => "失败",
        _ => "未执行"
    };
}
