namespace TmcContractGenerator;

internal static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            var configIndex = Array.IndexOf(args, "--config");
            if (configIndex < 0 || configIndex + 1 >= args.Length)
                throw new GeneratorException("Usage: TmcContractGenerator --config <config.json>");

            var result = ContractGenerator.Generate(Path.GetFullPath(args[configIndex + 1]));
            Console.WriteLine("TMC symbols found: {0}", result.FoundSymbols);
            Console.WriteLine("Contract symbols generated: {0}", result.GeneratedSymbols);
            Console.WriteLine("Roots: {0}", string.Join(", ", result.Roots));
            foreach (var warning in result.Warnings)
                Console.Error.WriteLine("warning: " + warning);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("error: " + ex.Message);
            return 1;
        }
    }
}
