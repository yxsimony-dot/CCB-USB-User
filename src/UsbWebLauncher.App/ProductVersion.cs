using System.Reflection;
namespace UsbWebLauncher.App;
internal static class ProductVersion
{
    internal static string Display => typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
        .InformationalVersion.Split('+')[0] ?? "未知版本";
}
