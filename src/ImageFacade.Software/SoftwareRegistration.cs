using System.Runtime.CompilerServices;
using ImageFacade.Backends;

namespace ImageFacade.Software;

/// <summary>
/// 引用 <c>ImageFacade.Software</c> 就自动获得"缺能力时回退到软后端"的行为。
/// </summary>
/// <remarks>
/// 模块初始化器在本程序集被加载时执行——如果应用只是引用但从未触碰任何
/// <c>ImageFacade.Software</c> 类型，注册可能尚未发生。需要确定性时在启动处显式调用
/// <c>SoftwareBackend.UseAsFallback()</c>（幂等）。
/// </remarks>
internal static class SoftwareRegistration
{
    [ModuleInitializer]
    internal static void Register() => SoftwareBackend.UseAsFallback();
}
