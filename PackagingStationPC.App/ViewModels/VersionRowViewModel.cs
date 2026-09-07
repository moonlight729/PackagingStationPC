namespace PackagingStationPC.ViewModels;

public sealed record VersionRowViewModel(
    string Component,
    string Field,
    string Value,
    string State,
    string Source);
