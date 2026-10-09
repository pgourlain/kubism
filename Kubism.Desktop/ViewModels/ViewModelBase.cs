using CommunityToolkit.Mvvm.ComponentModel;

namespace Kubism.Desktop.ViewModels;

public abstract class ViewModelBase : ObservableObject;

public enum Severity { Neutral, Ok, Warning, Error }

/// <summary>Anything that owns cluster work and must stop it when closed.</summary>
public interface IClosable : IAsyncDisposable;
