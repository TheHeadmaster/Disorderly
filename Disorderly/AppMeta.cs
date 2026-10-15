using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Disorderly;

/// <summary>
/// Contains information about the application such as the version, the AppData directory, whether it is running in debug, etc.
/// </summary>
public static partial class AppMeta
{

    /// <summary>
    /// Gets whether the program is running in debug mode.
    /// </summary>
    public static bool IsDebug =>
#if DEBUG
            true;
#else
            false;
#endif

    /// <summary>
    /// Gets the version of the application.
    /// </summary>
    public static string Version
    {
        get
        {
            if (field is not null)
            {
                return field;
            }

            Assembly assembly = Assembly.GetExecutingAssembly();

            AssemblyInformationalVersionAttribute? informationalVersionAttribute = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();

            if (informationalVersionAttribute is null)
            {
                field = string.Empty;

                return field;
            }

            string productVersion = informationalVersionAttribute.InformationalVersion;

            Regex regex = VersionRegex();

            Match match = regex.Match(productVersion);

            field = !match.Success ? string.Empty : match.Groups[1].Value;

            return field;
        }
    }

    /// <summary>
    /// Gets the AppData directory for this application.
    /// </summary>
    public static string AppDataDirectory
    {
        get
        {
            if (field is not null)
            {
                return field;
            }

            field = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Disorderly");

            return field;
        }
    }

    /// <summary>
    /// Gets the User directory for this application.
    /// </summary>
    public static string UserDirectory
    {
        get
        {
            if (field is not null)
            {
                return field;
            }

            field = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "Disorderly");

            return field;
        }
    }

    /// <summary>
    /// Gets the Logs directory for this application.
    /// </summary>
    public static string LogsDirectory => Path.Combine(AppDataDirectory, "Logs");

    [GeneratedRegex("([^+]+)(?:\\+[0-9a-zA-Z]+)?")]
    private static partial Regex VersionRegex();
}