using Mono.Cecil;

namespace SkyForcePatcher;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            var options = CliOptions.Parse(args);
            if (options is null)
            {
                PrintUsage();
                return 1;
            }

            var config = ModConfig.Load(options.ConfigPath);

            using var module = ModuleDefinition.ReadModule(options.InputPath, new ReaderParameters
            {
                InMemory = true,
                ReadingMode = ReadingMode.Immediate,
            });

            var applied = Patcher.Apply(module, config);

            if (applied.Count == 0)
            {
                Console.Error.WriteLine("没有应用任何补丁（配置全是默认值？）");
                return 2;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.OutputPath))!);
            module.Write(options.OutputPath);

            Console.WriteLine($"输入: {options.InputPath}");
            Console.WriteLine($"输出: {options.OutputPath}");
            Console.WriteLine("已应用:");
            foreach (var line in applied)
            {
                Console.WriteLine($"  · {line}");
            }

            return 0;
        }
        catch (PatchException ex)
        {
            Console.Error.WriteLine($"补丁失败: {ex.Message}");
            return 3;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"错误: {ex.Message}");
            return 4;
        }
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("""
                                用法:
                                  skyforce-patcher --input <Assembly-CSharp.dll> --output <out.dll> --config <config.json>
                                """);
    }
}

internal sealed record CliOptions(string InputPath, string OutputPath, string ConfigPath)
{
    public static CliOptions? Parse(string[] args)
    {
        string? input = null, output = null, config = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--input" when i + 1 < args.Length:
                    input = args[++i];
                    break;
                case "--output" when i + 1 < args.Length:
                    output = args[++i];
                    break;
                case "--config" when i + 1 < args.Length:
                    config = args[++i];
                    break;
            }
        }

        return input is not null && output is not null && config is not null
            ? new CliOptions(input, output, config)
            : null;
    }
}
