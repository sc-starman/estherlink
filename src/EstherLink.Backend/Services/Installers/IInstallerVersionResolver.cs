namespace EstherLink.Backend.Services.Installers;

public interface IInstallerVersionResolver
{
    bool TryResolveWindowsMsiVersion(string msiPath, out string version, out string error);
}
