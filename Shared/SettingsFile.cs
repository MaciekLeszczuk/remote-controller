using System.Text.Json;

// Small JSON settings files kept in %APPDATA%\ControllerRelay.
static class SettingsFile
{
    static string PathFor(string fileName) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ControllerRelay", fileName);

    public static T Load<T>(string fileName) where T : new()
    {
        try { return JsonSerializer.Deserialize<T>(File.ReadAllText(PathFor(fileName))) ?? new T(); }
        catch (Exception) { return new T(); }
    }

    public static void Save<T>(string fileName, T value)
    {
        try
        {
            var path = PathFor(fileName);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(value));
        }
        catch (Exception) { }
    }
}
