using System.Reflection;
using System.Runtime.InteropServices;

namespace EstherLink.Backend.Services.Installers;

public sealed class WindowsInstallerVersionResolver : IInstallerVersionResolver
{
    public bool TryResolveWindowsMsiVersion(string msiPath, out string version, out string error)
    {
        version = string.Empty;
        error = string.Empty;

        if (!File.Exists(msiPath))
        {
            error = "Installer file was not found.";
            return false;
        }

        if (!OperatingSystem.IsWindows())
        {
            error = "Automatic MSI ProductVersion extraction is only supported on Windows hosts. Pass version explicitly.";
            return false;
        }

        object? installer = null;
        object? database = null;
        object? view = null;
        object? record = null;

        try
        {
            var installerType = Type.GetTypeFromProgID("WindowsInstaller.Installer", throwOnError: false);
            if (installerType is null)
            {
                error = "Windows Installer COM object is unavailable.";
                return false;
            }

            installer = Activator.CreateInstance(installerType);
            if (installer is null)
            {
                error = "Failed to initialize Windows Installer COM object.";
                return false;
            }

            database = installerType.InvokeMember(
                "OpenDatabase",
                BindingFlags.InvokeMethod,
                null,
                installer,
                [msiPath, 0]);

            if (database is null)
            {
                error = "Failed to open MSI database.";
                return false;
            }

            view = database.GetType().InvokeMember(
                "OpenView",
                BindingFlags.InvokeMethod,
                null,
                database,
                ["SELECT `Value` FROM `Property` WHERE `Property`='ProductVersion'"]);

            if (view is null)
            {
                error = "Failed to query MSI property table.";
                return false;
            }

            view.GetType().InvokeMember("Execute", BindingFlags.InvokeMethod, null, view, null);
            record = view.GetType().InvokeMember("Fetch", BindingFlags.InvokeMethod, null, view, null);

            if (record is null)
            {
                error = "MSI ProductVersion is missing.";
                return false;
            }

            var value = record.GetType().InvokeMember(
                "StringData",
                BindingFlags.GetProperty,
                null,
                record,
                [1])?.ToString()?.Trim();

            if (string.IsNullOrWhiteSpace(value))
            {
                error = "MSI ProductVersion is empty.";
                return false;
            }

            version = value;
            return true;
        }
        catch (Exception ex)
        {
            error = $"Failed to read MSI ProductVersion: {ex.Message}";
            return false;
        }
        finally
        {
            ReleaseCom(record);
            ReleaseCom(view);
            ReleaseCom(database);
            ReleaseCom(installer);
        }
    }

    private static void ReleaseCom(object? value)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }
}
