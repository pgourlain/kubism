using Kubism.Desktop.Navigation;

namespace Kubism.Desktop.ViewModels;

/// <summary>One line of the history menu.</summary>
public sealed record HistoryItem(NavEntry Entry, string Title, bool IsCurrent);
