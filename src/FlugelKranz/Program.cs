using System.CommandLine;
using Avalonia;
using FlugelKranz.OpenXR;
using FlugelKranz.OpenVR;
using FlugelKranz.Core;

namespace FlugelKranz;

internal static class Program
{
    public static string MonadoLibraryPath { get; private set; } = MonadoRuntimeLocator.Resolve();

    [STAThread]
    public static int Main(string[] args)
    {
        var libraryOption = new Option<string>("--lib-monado", "LibMonadoPath", "lib")
        {
            Description = "libmonado.so / libmonado_wivrn.so のパス",
            HelpName = "PATH",
            Arity = ArgumentArity.ExactlyOne,
            DefaultValueFactory = _ => MonadoRuntimeLocator.Resolve()
        };
        libraryOption.Validators.Add(result =>
        {
            var path = result.GetValueOrDefault<string>();
            if (string.IsNullOrWhiteSpace(path) || path.StartsWith('-'))
                result.AddError("--lib-monado にはライブラリのパスを指定してください。'-' で始まるファイル名には './' を付けてください。");
        });
        var diagnoseOption = new Option<bool>("--diagnose")
        {
            Description = "UI を開かず接続・入力を確認（空間の書き込みなし）"
        };
        var root = new RootCommand("FlugelKranz — 自由飛行 / 無限歩行。Windows x64 + SteamVR / Linux Wayland + Monado。");
        root.Options.Add(libraryOption);
        root.Options.Add(diagnoseOption);
        root.SetAction(result =>
        {
            MonadoLibraryPath = result.GetValue(libraryOption)!;
            return Run(result.GetValue(diagnoseOption));
        });

        // Keep Avalonia startup on the entry thread by invoking the action synchronously.
        return root.Parse(args).Invoke();
    }

    private static int Run(bool diagnose)
    {
        try
        {
            if (diagnose)
            {
                using IFlightRuntime runtime = OperatingSystem.IsWindows()
                    ? new OpenVrFlightRuntime()
                    : new MonadoFlightRuntime(MonadoLibraryPath);
                if (runtime is MonadoFlightRuntime monado)
                    foreach (var origin in monado.TrackingOrigins)
                        Console.WriteLine($"原点 {origin.Index}: 接続時={origin.Original} 現在={origin.Current}");
                for (int i = 0; i < 1000; i++)
                {
                    var frame = runtime.ReadPhysical();
                    if (i % 100 == 0)
                    {
                        Console.WriteLine($"追跡: HMD={frame.HeadTracked}, 左={frame.Left.IsTracked}, 右={frame.Right.IsTracked}");
                        if (runtime is OpenVrFlightRuntime steamVr)
                            Console.WriteLine(steamVr.InputDiagnostics);
                    }
                    if (frame.HeadTracked && frame.Left.IsTracked && frame.Right.IsTracked)
                    { Console.WriteLine("ランタイム接続、基準空間、HMD・両手の入力を確認しました。空間は変更していません。"); return 0; }
                    Thread.Sleep(10);
                }
                Console.Error.WriteLine("接続できましたが、HMD・両手の有効な入力を確認できませんでした。");
                return 1;
            }
            if (!OperatingSystem.IsWindows() && (!OperatingSystem.IsLinux() || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))))
                throw new InvalidOperationException("Linux の Wayland セッションが必要です（WAYLAND_DISPLAY が未設定）。");
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime([]);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>().UseSkia().UseHarfBuzz();
        return (OperatingSystem.IsWindows() ? builder.UseWin32() : builder.UseWayland()).LogToTrace();
    }
}
