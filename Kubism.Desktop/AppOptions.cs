namespace Kubism.Desktop;

public sealed class AppOptions
{
    public static AppOptions Current { get; set; } = new();
    public bool Demo { get; init; }
    public string? DataFolder { get; init; }

    public static AppOptions Parse(string[] args) => new()
    {
        Demo = args.Contains("--demo"),
        DataFolder = args.SkipWhile(a => a != "--data").Skip(1).FirstOrDefault(),
    };
}
